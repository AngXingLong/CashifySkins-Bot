using SteamKit2;
using SteamTrade;
using SteamTrade.TradeOffer;
using System;
using System.Collections.Generic;
using TradeAsset = SteamTrade.TradeOffer.TradeOffer.TradeStatusUser.TradeAsset;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using StackExchange.Redis;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Text.RegularExpressions;

namespace SteamBot
{

    public class WebsiteCookieHandler : UserHandler
    {

        public WebsiteCookieHandler(Bot bot, SteamID sid) : base(bot, sid) { }

        public override void OnLoginCompleted()
        {
             Task.Factory.StartNew(() => StartRedis());    
        }

        public void StartRedis()
        {
            IDatabase db = RedisNetwork.Redis.GetDatabase();

            while (true)
            {
                try
                {
                    if (Bot.CheckCookies())
                    {
                        string cookieString = "steamLogin=" + SteamWeb.Token + "; steamLoginSecure=" + SteamWeb.TokenSecure + "; sessionid=" + SteamWeb.SessionId;
                        db.StringSet("auth_cookie", cookieString);
                    }
                }
                catch (Exception e)
                {
                    db = RedisNetwork.Redis.GetDatabase();
                    Log.Error("Redis error" + e);
                }
                Thread.Sleep(1000*60);
            }
        }

        public override void OnMessage(string message, EChatEntryType type) { }

        public override bool OnGroupAdd() { return false; }

        public override bool OnFriendAdd() { return IsAdmin; }

        public override void OnFriendRemove() { }

        public override bool OnTradeRequest() { return false; }

        public override void OnTradeError(string error) { }

        public override void OnTradeTimeout() { }

        public override void OnTradeAwaitingConfirmation(long tradeOfferID) { }

        public override void OnTradeInit() { }

        public override void OnTradeAddItem(Schema.Item schemaItem, Inventory.Item inventoryItem) { }

        public override void OnTradeRemoveItem(Schema.Item schemaItem, Inventory.Item inventoryItem) { }

        public override void OnTradeMessage(string message) { }

        public override void OnTradeReady(bool ready) { }

        public override void OnTradeAccept() { }

        public override void OnTradeOfferUpdated(TradeOffer offer)
        {
            if (offer.OfferState == TradeOfferState.TradeOfferStateActive && IsAdmin)
            {
                Log.Info("Offer accepted");
                offer.Accept();
                Bot.AcceptAllMobileTradeConfirmations();
            }
        }


    }
}
