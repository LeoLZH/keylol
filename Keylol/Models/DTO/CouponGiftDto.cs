﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Newtonsoft.Json;

namespace Keylol.Models.DTO
{
    /// <summary>
    /// CouponGift DTO
    /// </summary>
    public class CouponGiftDto
    {
        /// <summary>
        /// 创建空 DTO，需要手动填充
        /// </summary>
        public CouponGiftDto()
        {
        }

        /// <summary>
        /// 创建 DTO 并自动填充部分数据
        /// </summary>
        /// <param name="gift"><see cref="CouponGift"/> 对象</param>
        public CouponGiftDto(CouponGift gift)
        {
            Id = gift.Id;
            Name = gift.Name;
            Descriptions = JsonConvert.DeserializeObject<List<string>>(gift.Descriptions);
            ThumbnailImage = gift.ThumbnailImage;
            PreviewImage = gift.PreviewImage;
            AcceptedFields = JsonConvert.DeserializeObject<List<CouponGiftAcceptedFieldDto>>(gift.AcceptedFields);
            Price = gift.Price;
        }

        /// <summary>
        /// Id
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 名称
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 描述
        /// </summary>
        public List<string> Descriptions { get; set; }

        /// <summary>
        /// 缩略图
        /// </summary>
        public string ThumbnailImage { get; set; }

        /// <summary>
        /// 预览图片
        /// </summary>
        public string PreviewImage { get; set; }

        /// <summary>
        /// 接受的用户输入字段
        /// </summary>
        public List<CouponGiftAcceptedFieldDto> AcceptedFields { get; set; }

        /// <summary>
        /// 价格
        /// </summary>
        public int? Price { get; set; }

        /// <summary>
        /// 被兑换的总次数
        /// </summary>
        public int? RedeemCount { get; set; }

        /// <summary>
        /// 是否已被当前用户兑换过
        /// </summary>
        public bool? Redeemed { get; set; }

        /// <summary>
        /// 用户此前兑换输入过的字段
        /// </summary>
        public dynamic Extra { get; set; }
    }

    /// <summary>
    /// 用于 <see cref="CouponGiftDto"/> AcceptedFields
    /// </summary>
    public class CouponGiftAcceptedFieldDto
    {
        /// <summary>
        /// 字段 ID
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 字段名称
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// 字段描述
        /// </summary>
        public string Description { get; set; }
    }
}