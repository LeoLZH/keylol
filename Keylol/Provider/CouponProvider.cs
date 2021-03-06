﻿using System;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading.Tasks;
using Keylol.Hubs;
using Keylol.Identity;
using Keylol.Models;
using Keylol.Models.DAL;
using Newtonsoft.Json;

namespace Keylol.Provider
{
    /// <summary>
    ///     提供文券操作服务
    /// </summary>
    public class CouponProvider
    {
        private readonly KeylolDbContext _dbContext;
        private readonly KeylolUserManager _userManager;

        /// <summary>
        ///     创建新 <see cref="CouponProvider" />
        /// </summary>
        /// <param name="dbContext">
        ///     <see cref="KeylolDbContext" />
        /// </param>
        /// <param name="userManager">
        ///     <see cref="KeylolUserManager" />
        /// </param>
        public CouponProvider(KeylolDbContext dbContext, KeylolUserManager userManager)
        {
            _dbContext = dbContext;
            _userManager = userManager;
        }

        /// <summary>
        ///     根据文券事件更新用户的文券数量
        /// </summary>
        /// <param name="user">用户对象</param>
        /// <param name="event">文券事件</param>
        /// <param name="description">文券记录描述，会被序列化成 JSON 存储到数据库</param>
        /// <param name="logTime">文券日志记录时间，如果为 null 则使用当前时间</param>
        /// <exception cref="ArgumentNullException">user 参数为 null</exception>
        public async Task UpdateAsync(KeylolUser user, CouponEvent @event, object description = null,
            DateTime? logTime = null)
        {
            await UpdateAsync(user, @event, @event.ToCouponChange(), description, logTime);
        }

        /// <summary>
        ///     增减用户的文券数量
        /// </summary>
        /// <param name="user">用户对象</param>
        /// <param name="event">文券事件</param>
        /// <param name="change">文券数量变化，正数为增加，负数为减少</param>
        /// <param name="description">文券记录描述</param>
        /// <param name="logTime">文券日志记录时间，如果为 null 则使用当前时间</param>
        /// <exception cref="ArgumentNullException">user 参数为 null</exception>
        public async Task UpdateAsync(KeylolUser user, CouponEvent @event, int change, object description = null,
            DateTime? logTime = null)
        {
            if (user == null)
                throw new ArgumentNullException(nameof(user));

            var log = new CouponLog
            {
                Change = change,
                Event = @event,
                UserId = user.Id,
                Description = JsonConvert.SerializeObject(description)
            };
            _dbContext.CouponLogs.Add(log);

            bool saveFailed;
            do
            {
                try
                {
                    saveFailed = false;
                    user.Coupon += log.Change;
                    log.Balance = user.Coupon;
                    log.CreateTime = logTime ?? DateTime.Now;
                    await _dbContext.SaveChangesAsync();
                    NotificationProvider.Hub<CouponHub, ICouponHubClient>().User(user.Id)?
                        .OnCouponChanged(log.Event.ToString(), log.Change, log.Balance);
                }
                catch (DbUpdateConcurrencyException e)
                {
                    saveFailed = true;
                    await e.Entries.Single().ReloadAsync();
                }
            } while (saveFailed);
        }

        /// <summary>
        ///     判断指定用户是否有足够文券触发指定事件
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="event">文券事件</param>
        /// <returns>可以触发指定事件返回 true，不能则返回 false</returns>
        public async Task<bool> CanTriggerEventAsync(string userId, CouponEvent @event)
        {
            var user = await _userManager.FindByIdAsync(userId);
            return user.Coupon + @event.ToCouponChange() >= 0;
        }
    }

    /// <summary>
    ///     CouponEvent 的一些常用扩展
    /// </summary>
    public static class CouponEventExtensions
    {
        /// <summary>
        ///     获取指定事件的文券变动量
        /// </summary>
        /// <param name="event">文券事件</param>
        /// <returns>变动量，可以为正数或者负数</returns>
        public static int ToCouponChange(this CouponEvent @event)
        {
            switch (@event)
            {
                case CouponEvent.新注册:
                    return 10;

                case CouponEvent.应邀注册:
                    return 5;

                case CouponEvent.发布文章:
                    return -3;

                case CouponEvent.发出认可:
                    return -1;

                case CouponEvent.获得认可:
                    return 1;

                case CouponEvent.每日访问:
                    return 1;

                case CouponEvent.邀请注册:
                    return 3;

                default:
                    throw new ArgumentOutOfRangeException(nameof(@event), @event, null);
            }
        }
    }
}