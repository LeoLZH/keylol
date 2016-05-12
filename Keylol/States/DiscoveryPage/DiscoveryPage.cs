﻿using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Keylol.Models;
using Keylol.Models.DAL;
using Keylol.Provider.CachedDataProvider;

namespace Keylol.States.DiscoveryPage
{
    /// <summary>
    /// 发现页
    /// </summary>
    public class DiscoveryPage
    {
        /// <summary>
        /// 创建 <see cref="DiscoveryPage"/>
        /// </summary>
        /// <param name="currentUserId">当前登录用户 ID</param>
        /// <param name="dbContext"><see cref="KeylolDbContext"/></param>
        /// <param name="cachedData"><see cref="CachedDataProvider"/></param>
        /// <returns><see cref="DiscoveryPage"/></returns>
        public static async Task<DiscoveryPage> CreateAsync(string currentUserId, KeylolDbContext dbContext,
            CachedDataProvider cachedData)
        {
            return new DiscoveryPage
            {
                SlideshowEntries = await SlideshowEntryList.CreateAsync(dbContext),
                SpotlightPoints = await SpotlightPointList.CreateAsync(currentUserId, dbContext, cachedData),
                SpotlightReviews = await SpotlightArticleList.CreateAsync(currentUserId,
                    SpotlightArticleStream.ArticleCategory.Review, dbContext, cachedData),
                SpotlightConferences = await SpotlightConferenceList.CreateAsync(dbContext),
                SpotlightStudies = await SpotlightArticleList.CreateAsync(currentUserId,
                    SpotlightArticleStream.ArticleCategory.Study, dbContext, cachedData),
                SalesTodayHeaderImage = await (from feed in dbContext.Feeds
                    where feed.StreamName == OnSalePointStream.Name
                    join point in dbContext.Points on feed.Entry equals point.Id
                    orderby feed.Id descending
                    select point.HeaderImage).FirstOrDefaultAsync(),
                SalesTodayPageCount = await OnSalePointList.PageCountAsync(dbContext),
                SalesToday = await OnSalePointList.CreateAsync(currentUserId, 1, dbContext, cachedData),
                SpotlightStories = await SpotlightArticleList.CreateAsync(currentUserId,
                    SpotlightArticleStream.ArticleCategory.Story, dbContext, cachedData),
                LatestArticlePageCount = await LatestArticleList.PageCountAsync(dbContext),
                LatestArticles = await LatestArticleList.CreateAsync(1, dbContext, cachedData)
            };
        }

        /// <summary>
        /// 滑动展柜
        /// </summary>
        public SlideshowEntryList SlideshowEntries { get; set; }

        /// <summary>
        /// 精选据点
        /// </summary>
        public SpotlightPointList SpotlightPoints { get; set; }

        /// <summary>
        /// 精选评测
        /// </summary>
        public SpotlightArticleList SpotlightReviews { get; set; }

        /// <summary>
        /// 精选专题
        /// </summary>
        public SpotlightConferenceList SpotlightConferences { get; set; }

        /// <summary>
        /// 精选研究
        /// </summary>
        public SpotlightArticleList SpotlightStudies { get; set; }

        /// <summary>
        /// 是日优惠头部图
        /// </summary>
        public string SalesTodayHeaderImage { get; set; }

        /// <summary>
        /// 是日优惠总数
        /// </summary>
        public int SalesTodayPageCount { get; set; }

        /// <summary>
        /// 是日优惠
        /// </summary>
        public OnSalePointList SalesToday { get; set; }

        /// <summary>
        /// 精选谈论
        /// </summary>
        public SpotlightArticleList SpotlightStories { get; set; }

        /// <summary>
        /// 最新文章总数
        /// </summary>
        public int LatestArticlePageCount { get; set; }

        /// <summary>
        /// 最新文章
        /// </summary>
        public LatestArticleList LatestArticles { get; set; }
    }
}