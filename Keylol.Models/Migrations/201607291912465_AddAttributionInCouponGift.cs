namespace Keylol.Models.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class AddAttributionInCouponGift : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.CouponGifts", "Value", c => c.Int(nullable: false));
        }
        
        public override void Down()
        {
            DropColumn("dbo.CouponGifts", "Value");
        }
    }
}
