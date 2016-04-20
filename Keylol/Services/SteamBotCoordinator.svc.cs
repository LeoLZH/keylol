﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.ServiceModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Keylol.Hubs;
using Keylol.Models.DAL;
using Keylol.Models.DTO;
using Keylol.ServiceBase;
using Keylol.Services.Contracts;
using Keylol.Utilities;
using Microsoft.AspNet.SignalR;
using Microsoft.Practices.EnterpriseLibrary.TransientFaultHandling;
using Newtonsoft.Json.Linq;
using RabbitMQ.Client;

namespace Keylol.Services
{
    /// <summary>
    /// <see cref="ISteamBotCoordinator"/> 实现
    /// </summary>
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.PerSession, ConcurrencyMode = ConcurrencyMode.Multiple)]
    public class SteamBotCoordinator : ISteamBotCoordinator
    {
        private readonly RetryPolicy _retryPolicy;
        private readonly IModel _mqChannel;
        private readonly HttpClient _httpClient = new HttpClient();
        private static readonly object AllocationLock = new object(); // 在真正分配机器人前必须取得该锁

        /// <summary>
        /// 所有正在进行的会话，Key 为 Session ID，Value 为 <see cref="SteamBotCoordinator"/>
        /// </summary>
        public static ConcurrentDictionary<string, SteamBotCoordinator> Sessions { get; } =
            new ConcurrentDictionary<string, SteamBotCoordinator>();

        /// <summary>
        /// 当前会话 ID
        /// </summary>
        public string SessionId { get; } = OperationContext.Current.SessionId;

        /// <summary>
        /// 当前会话的客户端
        /// </summary>
        public ISteamBotCoordinatorCallback Client { get; } =
            OperationContext.Current.GetCallbackChannel<ISteamBotCoordinatorCallback>();

        /// <summary>
        /// 创建 <see cref="SteamBotCoordinator"/>
        /// </summary>
        public SteamBotCoordinator(RetryPolicy retryPolicy, IModel mqChannel)
        {
            _retryPolicy = retryPolicy;
            _mqChannel = mqChannel;

            Sessions[SessionId] = this;
            OperationContext.Current.InstanceContext.Closing += OnSessionEnd;
            OnSessionBegin();
        }

        private async void OnSessionBegin()
        {
            // 更新已分配机器人的 SessionId
            try
            {
                var allocatedBots = await Client.GetAllocatedBots();
                if (allocatedBots != null)
                {
                    using (var dbContext = new KeylolDbContext())
                    {
                        var bots = await dbContext.SteamBots.Where(b => allocatedBots.Contains(b.Id)).ToListAsync();
                        foreach (var bot in bots)
                        {
                            if (Sessions.ContainsKey(bot.SessionId))
                            {
                                await Sessions[bot.SessionId].Client.StopBot(bot.Id);
                            }
                            bot.SessionId = SessionId;
                        }
                        await dbContext.SaveChangesAsync();
                    }
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private async void OnSessionEnd(object sender, EventArgs eventArgs)
        {
            try
            {
                _mqChannel.Dispose();
                SteamBotCoordinator coordinator;
                Sessions.TryRemove(SessionId, out coordinator);
                if (Sessions.Count > 0)
                    await ReallocateBots(Sessions.Values.Last());
            }
            catch (Exception)
            {
                // ignore
            }
        }

        /// <summary>
        /// 重新计算每个客户端应该分配的机器人数量并通知客户端
        /// </summary>
        /// <param name="newSession">新 Session，在分配机器人时会最后分配</param>
        private static async Task ReallocateBots(SteamBotCoordinator newSession)
        {
            try
            {
                using (var dbContext = new KeylolDbContext())
                {
                    var botCount = await dbContext.SteamBots.CountAsync(b => b.Enabled);
                    var averageCount = botCount/Sessions.Count;
                    var lastCount = botCount - averageCount*(Sessions.Count - 1);
                    foreach (var session in Sessions.Values.Where(s => s != newSession))
                    {
                        await session.Client.RequestReallocateBots(averageCount);
                    }
                    await newSession.Client.RequestReallocateBots(lastCount);
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        /// <summary>
        /// 请求分配机器人，协作器在计算好机器人数量后将通过 RequestReallocateBots 回调通知客户端
        /// </summary>
        public async Task RequestBots()
        {
            await ReallocateBots(this);
        }

        /// <summary>
        /// 请求分配指定数量的机器人
        /// </summary>
        /// <param name="count">要求分配的数量</param>
        /// <returns>分配给客户端的机器人列表</returns>
        public List<SteamBotDto> AllocateBots(int count)
        {
            lock (AllocationLock)
                using (var dbContext = new KeylolDbContext())
                {
                    // 机器人的 SessionId 不存在 Sessions 中则认定为没有被分配过
                    var bots = dbContext.SteamBots.Where(bot => !Sessions.Keys.Contains(bot.SessionId) && bot.Enabled)
                        .Take(() => count)
                        .ToList();
                    foreach (var bot in bots)
                    {
                        bot.SessionId = SessionId;
                    }
                    dbContext.SaveChanges();
                    return bots.Select(bot => new SteamBotDto(bot, true)).ToList();
                }
        }

        /// <summary>
        /// 更新指定用户的属性
        /// </summary>
        /// <param name="steamId">要更新的用户 Steam ID</param>
        /// <param name="profileName">Steam 昵称，<c>null</c> 表示不更新</param>
        public async Task UpdateUser(string steamId, string profileName)
        {
            using (var dbContext = new KeylolDbContext())
            {
                var user = await dbContext.Users.SingleOrDefaultAsync(u => u.SteamId == steamId);
                if (user == null)
                    return;
                if (profileName != null)
                    user.SteamProfileName = profileName;
                await dbContext.SaveChangesAsync();
            }
        }

        /// <summary>
        /// 更新指定机器人的属性
        /// </summary>
        /// <param name="id">要更新的机器人 ID</param>
        /// <param name="friendCount">好友数，<c>null</c> 表示不更新</param>
        /// <param name="online">是否在线，<c>null</c> 表示不更新</param>
        /// <param name="steamId">Steam ID，<c>null</c> 表示不更新</param>
        public async Task UpdateBot(string id, int? friendCount = null, bool? online = null, string steamId = null)
        {
            using (var dbContext = new KeylolDbContext())
            {
                var bot = await dbContext.SteamBots.FindAsync(id);
                if (bot == null)
                    return;
                if (friendCount != null)
                    bot.FriendCount = friendCount.Value;
                if (online != null)
                    bot.Online = online.Value;
                if (steamId != null)
                    bot.SteamId = steamId;
                await dbContext.SaveChangesAsync();
            }
        }

        /// <summary>
        /// 判断指定 Steam 账户是不是其乐用户并且匹配指定机器人
        /// </summary>
        /// <param name="steamId">Steam ID</param>
        /// <param name="botId">机器人 ID</param>
        /// <returns><c>true</c> 表示是其乐用户并于目标机器人匹配，<c>false</c> 表示不是</returns>
        public async Task<bool> IsKeylolUser(string steamId, string botId)
        {
            using (var dbContext = new KeylolDbContext())
            {
                return await dbContext.Users.AnyAsync(u => u.SteamId == steamId && u.SteamBotId == botId);
            }
        }

        /// <summary>
        /// 当机器人接收到用户好友请求时，通过此方法通知协调器
        /// </summary>
        /// <param name="userSteamId">用户 Steam ID</param>
        /// <param name="botId">机器人 ID</param>
        public async Task OnBotNewFriendRequest(string userSteamId, string botId)
        {
            await Client.AddFriend(botId, userSteamId); // 先接受请求
            using (var dbContext = new KeylolDbContext())
            {
                var user = await dbContext.Users.Where(u => u.SteamId == userSteamId).SingleOrDefaultAsync();
                if (user == null)
                {
                    // 非会员，在注册时绑定机器人
                    GlobalHost.ConnectionManager.GetHubContext<SteamBindingHub, ISteamBindingHubClient>()
                        .Clients.Clients(await dbContext.SteamBindingTokens.Where(t => t.BotId == botId)
                            .Select(t => t.BrowserConnectionId)
                            .ToListAsync())?
                        .NotifySteamFriendAdded();
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    await Client.SendChatMessage(botId, userSteamId,
                        "欢迎使用当前 Steam 账号加入其乐，请输入你在网页上获取的 8 位绑定验证码。");
                    _mqChannel.SendMessage(MqClientProvider.DelayedMessageExchange,
                        $"{MqClientProvider.SteamBotDelayedActionQueue}.{botId}", new SteamBotDelayedActionDto
                        {
                            Type = SteamBotDelayedActionType.RemoveFriend,
                            Properties = new
                            {
                                OnlyIfNotKeylolUser = true,
                                Message = "抱歉，你的会话因超时被强制结束，机器人已将你从好友列表中暂时移除。若要加入其乐，请重新按照网页指示注册账号。",
                                SteamId = userSteamId
                            }
                        }, 300000);
                }
                else
                {
                    // 现有会员添加机器人为好友
                    var bot = await dbContext.SteamBots.FindAsync(botId);
                    var userManager = KeylolUserManager.Create(dbContext);
                    if (bot != null && bot.FriendCount < bot.FriendUpperLimit &&
                        await userManager.GetStatusClaimAsync(user.Id) == StatusClaim.Probationer)
                    {
                        // 用户此前删除了机器人好友，重新设定当前机器人为绑定的机器人
                        user.SteamBotId = botId;
                        await dbContext.SaveChangesAsync(KeylolDbContext.ConcurrencyStrategy.DatabaseWin);
                        await userManager.RemoveStatusClaimAsync(user.Id);
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        await Client.SendChatMessage(botId, userSteamId,
                            "你已成功与其乐机器人再次绑定，请务必不要将其乐机器人从好友列表中移除。");
                    }
                    else
                    {
                        // 用户状态正常但是添加了新的机器人好友，应当移除好友
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        await Client.SendChatMessage(botId, userSteamId,
                            "你已绑定另一其乐机器人，当前机器人已拒绝你的好友请求。如有需要，你可以在其乐设置表单中找到你绑定的机器人帐号。");
                        await Client.RemoveFriend(botId, userSteamId);
                    }
                }
            }
        }

        /// <summary>
        /// 当用户与机器人不再为好友时，通过此方法通知协调器
        /// </summary>
        /// <param name="userSteamId">用户 Steam ID</param>
        /// <param name="botId">机器人 ID</param>
        public async Task OnUserBotRelationshipNone(string userSteamId, string botId)
        {
            using (var dbContext = new KeylolDbContext())
            {
                var user = await dbContext.Users.SingleOrDefaultAsync(u => u.SteamId == userSteamId);
                if (user == null)
                {
                    // 非会员不再为好友时，如果存在已绑定的 SteamBindingToken 则清除之
                    var bindingTokens = await dbContext.SteamBindingTokens
                        .Where(t => t.SteamId == userSteamId && t.BotId == botId)
                        .ToListAsync();
                    dbContext.SteamBindingTokens.RemoveRange(bindingTokens);
                    await dbContext.SaveChangesAsync();
                }
                else if (user.SteamBotId == botId)
                {
                    // 会员与自己的机器人不再为好友时，设置为暂准状态
                    var userManager = KeylolUserManager.Create(dbContext);
                    await userManager.SetStatusClaimAsync(user.Id, StatusClaim.Probationer);
                }
            }
        }

        /// <summary>
        /// 当机器人收到新的聊天消息时，通过此方法通知协调器
        /// </summary>
        /// <param name="senderSteamId">消息发送人 Steam ID</param>
        /// <param name="botId">机器人 ID</param>
        /// <param name="message">聊天消息内容</param>
        public async Task OnBotNewChatMessage(string senderSteamId, string botId, string message)
        {
            using (var dbContext = new KeylolDbContext())
            {
                var user = await dbContext.Users.Where(u => u.SteamId == senderSteamId).SingleOrDefaultAsync();
                if (user == null)
                {
                    // 非会员，只接受绑定验证码
                    var code = message.Trim();
                    var token = await dbContext.SteamBindingTokens
                        .SingleOrDefaultAsync(t => t.Code == code && t.SteamId == null);
                    if (token == null)
                    {
                        await Client.SendChatMessage(botId, senderSteamId,
                            "你的输入无法被识别，请确认登录验证码的长度和格式。如果需要帮助，请与其乐职员取得联系。");
                    }
                    else
                    {
                        token.BotId = botId;
                        token.SteamId = senderSteamId;
                        await dbContext.SaveChangesAsync();
                        GlobalHost.ConnectionManager.GetHubContext<SteamBindingHub, ISteamBindingHubClient>()
                            .Clients.Client(token.BrowserConnectionId)?
                            .NotifyCodeReceived(await Client.GetUserProfileName(botId, senderSteamId),
                                await Client.GetUserAvatarHash(botId, senderSteamId));
                        await Client.SendChatMessage(botId, senderSteamId,
                            "绑定成功，欢迎加入其乐！今后你可以向机器人发送对话快速登录社区，请勿将机器人从好友列表移除。");
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        await Client.SendChatMessage(botId, senderSteamId,
                            "若希望在其乐上获得符合游戏兴趣的据点推荐，请避免将 Steam 资料隐私设置为「仅自己可见」。");
                    }
                }
                else
                {
                    // 已有会员，接受登录验证码和调侃调戏
                    var match = Regex.Match(message, @"^\s*(\d{4})\s*$");
                    if (match.Success)
                    {
                        var code = match.Groups[1].Value;
                        var token = await dbContext.SteamLoginTokens.SingleOrDefaultAsync(t => t.Code == code);
                        if (token == null)
                        {
                            await Client.SendChatMessage(botId, senderSteamId,
                                "你的输入无法被识别，请确认登录验证码的长度和格式。如果需要帮助，请与其乐职员取得联系。");
                        }
                        else
                        {
                            token.SteamId = senderSteamId;
                            await dbContext.SaveChangesAsync();
                            GlobalHost.ConnectionManager.GetHubContext<SteamLoginHub, ISteamLoginHubClient>()
                                .Clients.Client(token.BrowserConnectionId)?
                                .NotifyCodeReceived();
                            await Client.SendChatMessage(botId, senderSteamId, "欢迎回来，你已成功登录其乐社区。");
                        }
                    }
                    else
                    {
                        await Client.SendChatMessage(botId, senderSteamId, await AskTulingBot(message, user.Id), true);
                    }
                }
            }
        }

        /// <summary>
        /// 心跳测试
        /// </summary>
        public void Ping()
        {
        }

        /// <summary>
        /// 询问图灵机器人问题
        /// </summary>
        /// <param name="question">问题内容</param>
        /// <param name="userId">上下文关联用户 ID</param>
        /// <returns>图灵机器人的回答，null 表示询问失败</returns>
        private async Task<string> AskTulingBot(string question, string userId)
        {
            const string apiKey = "51c3bd1bb6a9d092f8b63aca01262edf";
            using (var md5 = MD5.Create())
            {
                userId = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(userId)))
                    .Replace("-", "")
                    .ToLower();
            }
            JToken result = null;
            try
            {
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    result = JToken.Parse(await _httpClient.GetStringAsync(
                        $"http://www.tuling123.com/openapi/api?key={apiKey}&info={HttpUtility.UrlEncode(question)}&userid={userId}"));
                });
            }
            catch (Exception)
            {
                return null;
            }
            if (result == null)
                return null;
            switch ((int) result["code"])
            {
                case 200000: // 链接类
                    return $"{(string) result["text"]}\n{(string) result["url"]}";

                case 302000: // 新闻
                    return
                        $"{(string) result["text"]}\n{string.Join("\n\n", result["list"].Select(news => $"{news["article"]}\n{news["detailurl"]}"))}";

                case 305000: // 列车
                    return
                        $"{(string) result["text"]}\n{string.Join("\n\n", result["list"].Select(train => $"{train["trainnum"]}\n{train["start"]} --> {train["terminal"]}\n{train["starttime"]} --> {train["endtime"]}"))}";

                case 306000: // 航班
                    return
                        $"{(string) result["text"]}\n{string.Join("\n\n", result["list"].Select(flight => $"{flight["flight"]}\n{flight["starttime"]} --> {flight["endtime"]}"))}";

                case 308000: // 菜谱
                    return
                        $"{(string) result["text"]}\n{string.Join("\n\n", result["list"].Select(dish => $"{dish["name"]}\n{dish["info"]}\n{dish["detailurl"]}"))}";

                default:
                    return (string) result["text"];
            }
        }
    }
}