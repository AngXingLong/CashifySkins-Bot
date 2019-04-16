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

    public class WebsiteHandler : UserHandler
    {
        public WebsiteHandler(Bot bot, SteamID sid) : base(bot, sid) { }
        public TradeNetwork TradeNet;
        public bool startUpCompleted = false;
        public object cookieCheck = new object();
        public bool cookieReady = true;
        DateTime lastReported;
        DateTime lastFullUpdate;
        public int activeTransactions = 0;
        public bool acceptTransactions = true;
        List<int> allInventory = new List<int> { 753, 730, 570, 440 };

        public override void OnLoginCompleted()
        {
            Log.Warn("a");
            return;
            if (!startUpCompleted)
            {
                //Task.Factory.StartNew(() => Bot.FetchActiveIncomingOffers());
                TradeNet = new TradeNetwork(Bot.SteamClient.SteamID);
                TradeNet.ReportState();
                Random rnd = new Random();
                lastReported = DateTime.Now.AddMinutes(rnd.Next(5, 15));
                lastFullUpdate = DateTime.Now.AddMinutes(rnd.Next(30, 60));
                BotCleanUp();
                UpdateInventory();
                TradeNet.CheckSyncRequired();
                Task.Factory.StartNew(() => StartRedis());
                startUpCompleted = true;
            }
        }
        public bool stopAcceptingTransactions = false;

        public void UpdateBot()
        {
            Log.Info("Updating");
            lastReported = DateTime.Now.AddMinutes(10);
            TradeNet.ReportState();
            CookieCheck();
            Bot.DeclineAllIncomingOffers();

            if (TradeNet.CheckSyncRequired())
            {
                lastFullUpdate = DateTime.Now.AddHours(2);
                UpdateInventory();
            }
            else
            {
                if (DateTime.Now > lastFullUpdate)
                {
                    lastFullUpdate = DateTime.Now.AddHours(2);
                    UpdateInventory();
                }
                else
                {
                    UpdateInventory(null, false);
                }
            }
        }

        public void StartRedis()
        {
            IDatabase db = RedisNetwork.Redis.GetDatabase();

            string botsid = Convert.ToString(Bot.SteamClient.SteamID);
            db.KeyDelete(botsid);

            Log.Success("Bot listening for events");

            long transactionID = 0;

            while (true)
            {
                try
                {
                    if (activeTransactions > 4)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (stopAcceptingTransactions)
                    {
                        if (activeTransactions == 0)
                        {
                            if (!WorkerToManager.botsid.Contains(Bot.SteamClient.SteamID))
                            {
                                WorkerToManager.botsid.Add(Bot.SteamClient.SteamID);
                            }
                        }
                        continue;
                    }

                    transactionID = Convert.ToInt64(db.ListLeftPop(botsid));

                    if (transactionID == -1 || DateTime.Now > lastReported)
                    {
                        UpdateBot();
                        if (transactionID == -1) { continue; } // Check if bot is reporting
                    }

                    if (transactionID == 0)
                    {
                        Thread.Sleep(500);
                        continue;
                    }
                    else if (transactionID == -2)// Shutdown bot
                    {
                        stopAcceptingTransactions = true;
                    }
                    else
                    {
                        if (TradeNet.CheckValidTransaction(transactionID))
                        {
                            TradeNetwork.TradeInfo transaction = TradeNet.GetTradeInfo(transactionID);
                            Task.Factory.StartNew(() => StartTrade(transaction));
                        }
                        else
                        {
                            TradeNet.UpdateOfferStatus(transactionID, 5, "Timed Out", "");
                            Log.Warn("Transaction Invalid/TimedOut");
                        }
                    }
                }
                catch (Exception e)
                {
                    db = RedisNetwork.Redis.GetDatabase();
                    Log.Error("Redis Error at transaction ID: " + transactionID + " " + e.ToString());
                }
            }
        }

        public void BotCleanUp()
        {
            Bot.DeclineAllActiveOffers();
            TradeNet.RemoveProccesingTrade();
            List<TradeNetwork.TradeInfo> Incomplete = TradeNet.GetIncompleteTrade();

            foreach (var item in Incomplete)
            {

                string statusComment = "";
                string staffComment = "";
                string tradeId = "";
                int status = TradeOfferStatus(item.offerID, out statusComment, out staffComment, out tradeId, 0, 2);

                if (status == 9)
                {
                    if (item.tradeType == 0)
                    {
                        TradeNet.TradeFailure(item.transactionID, item.tradeType, 5, statusComment, staffComment);
                    }
                    else
                    {
                        TradeNet.TradeSuccess(item.transactionID, item.tradeType, statusComment, staffComment);
                    }
                }
                else if (status == 2)
                {
                    if (item.tradeType == 0)
                    {
                        TradeNetwork.TradeInfo transaction = TradeNet.GetTradeInfo(item.transactionID);
                        TradeNet.TradeSuccess(item.transactionID, item.tradeType, statusComment, staffComment, null);
                    }
                    else
                    {
                        TradeNet.TradeSuccess(item.transactionID, item.tradeType, statusComment, staffComment);
                    }

                }
                else
                {
                    TradeNet.TradeFailure(item.transactionID, item.tradeType, status, statusComment, staffComment);
                }
            }

        }

        public void CookieCheck()
        {

            if (Monitor.TryEnter(cookieCheck))
            {
                try
                {
                    cookieReady = false;
                    if (Bot.CheckCookies())
                    {
                        Thread.Sleep(10000);
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e.ToString());
                }
                Monitor.Exit(cookieCheck);
                cookieReady = true;
            }
            else
            {
                while (!cookieReady)
                {
                    Thread.Sleep(1000);
                }
            }
        }

        public void UpdateInventory(List<int> inventoryToRetrive = null, bool SyncDescription = true)
        {

            if (inventoryToRetrive == null) { inventoryToRetrive = allInventory; }

            try
            {
                foreach (int appid in inventoryToRetrive)
                {
                    GenericInventory myInventory = Bot.FetchInventory(Bot.SteamClient.SteamID, appid);
                    if (myInventory != null)
                    {
                        TradeNet.UpdateInventoryAssetId(myInventory, appid, SyncDescription);
                    }
                }

            }
            catch (Exception e)
            {
                Log.Error(e.ToString());
            }

        }

        public bool SteamAuthEnabled(SteamID steamid, string token)
        {
            for (int r = 2; r > 0; r--)
            {
                try
                {
                    if (Bot.GetEscrowDuration(steamid, token).DaysTheirEscrow > 0)
                    {
                        return false;
                    }
                    else
                    {
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        public void StartTrade(TradeNetwork.TradeInfo transaction)
        {
            activeTransactions++;
            try
            {
                if (transaction.tradeType == 0)
                {
                    if (!SteamAuthEnabled(transaction.receiver, transaction.offerToken))
                    {
                        TradeNet.TradeFailure(transaction.transactionID, transaction.tradeType, 4, "User Escrow Enabled", "");
                        goto end;
                    }
                }

                TradeOffer offer;
                List<TradeNetwork.Asset> tradeUsedAssets;

                if (!InitializeOffer(transaction.receiver, transaction.senderItems, transaction.receiverItems, out offer, out tradeUsedAssets))
                {
                    if (transaction.tradeType == 0)
                    {
                        TradeNet.TradeFailure(transaction.transactionID, transaction.tradeType, 4, "Insuffient Items", "");
                    }
                    else
                    {
                        TradeNet.TradeFailure(transaction.transactionID, transaction.tradeType, 5, "Unable To Fetch Items", "");
                    }

                    goto end;
                }

                string offerID = SendOffer(offer, transaction.offerToken, transaction.securityToken);

                if (String.IsNullOrEmpty(offerID))
                {
                    TradeNet.TradeFailure(transaction.transactionID, transaction.tradeType, 5, "Error Submiting Offer", "User inventory likely full");
                    goto end;
                }

                TradeNet.UpdateOfferStatus(transaction.transactionID, 2, "", "", offerID);
                AcceptMobileConfirmation(offerID);
                Log.Info("Offer Sent");

                string statusComment, staffComment;
                string tradeId = "";

                Thread.Sleep(15000);
                int status = TradeOfferStatus(offerID, out statusComment, out staffComment, out tradeId);

                /*
                List<int> inventoryToRetrive = new List<int>();

                if (transaction.receiverItems != null)
                {
                    foreach (int appid in transaction.receiverItems.Keys)
                    {
                        inventoryToRetrive.Add(appid);
                    }
                }

                if (transaction.senderItems != null)
                {
                    foreach (int appid in transaction.senderItems.Keys)
                    {
                        inventoryToRetrive.Add(appid);
                    }
                }

                //GetInventory(inventoryToRetrive);

                
                  if (transaction.tradeType != 0)
                    {
                        retriveInventoryLock.EnterReadLock();
                        var asset = tradeUsedAssets.FirstOrDefault();

                        if (!myInventory.CheckAssetID(asset.assetID, asset.appid))
                        {
                            retriveInventoryLock.ExitReadLock();
                            TradeNet.TradeSuccess(transaction.transactionID, transaction.tradeType, "", "Item asset missing, assume transaction complete");
                        }
                        else
                        {
                            retriveInventoryLock.ExitReadLock();
                            TradeNet.TradeFailure(transaction.transactionID, transaction.tradeType, status, statusComment, staffComment);
                        }

                    }
                    else
                    {
                        TradeNet.TradeFailure(transaction.transactionID, transaction.tradeType, status, statusComment, staffComment);
                    }
                */


                if (status == 4 || status == 5) //Error
                {
                    TradeNet.TradeFailure(transaction.transactionID, transaction.tradeType, status, statusComment, staffComment);
                }
                else if (status == 2) //Success
                {
                    if (transaction.tradeType == 0)
                    {
                        bool requireSync = false;

                        TradeNet.TradeSuccess(transaction.transactionID, transaction.tradeType, "", "", ParseAssetIdWReceipt(tradeId, transaction.receiverItems, out requireSync));

                        if (requireSync)
                        {
                            UpdateInventory(transaction.receiverItems.Select(x => x.Key).ToList());
                        }
                    }
                    else
                    {
                        TradeNet.TradeSuccess(transaction.transactionID, transaction.tradeType, "", "");
                    }
                }
                else if (status == 9)
                {
                    if (transaction.tradeType != 0)
                    {
                        TradeNet.TradeSuccess(transaction.transactionID, transaction.tradeType, statusComment, staffComment);
                    }
                    else
                    {
                        TradeNet.TradeFailure(transaction.transactionID, transaction.tradeType, 4, statusComment, staffComment);
                    }
                }
                end:
                Log.Info("Trade Complete");
            }
            catch (Exception e)
            {
                Log.Error("Error, Transaction ID: " + transaction.transactionID + " " + e);
            }

            activeTransactions--;
        }

        public bool InitializeOffer(ulong receiver, Dictionary<int, List<TradeNetwork.ItemDetails>> senderItems, Dictionary<int, List<TradeNetwork.ItemDetails>> receiverItems, out TradeOffer offer, out List<TradeNetwork.Asset> tradeUsedAssets)
        {
            Log.Info("Preparing Offer");

            offer = Bot.NewTradeOffer(receiver);
            tradeUsedAssets = new List<TradeNetwork.Asset>();

            if (receiverItems == null && senderItems == null) { return false; }

            if (receiverItems != null)
            {
                foreach (var appid in receiverItems)
                {
                    foreach (var item in appid.Value)
                    {
                        if (!offer.Items.AddTheirItemByAsset(item.assetid, appid.Key))
                        {
                            Log.Info("Insuffient Item from user");
                            return false;
                        }
                    }
                }
            }

            if (senderItems != null)
            {
                foreach (var appid in senderItems)
                {
                    foreach (var item in appid.Value)
                    {
                        if (!offer.Items.AddMyItemByAsset(item.assetid, appid.Key))
                        {
                            UpdateInventory(); // AssetID could have changed
                            Log.Warn("Insuffient Item from bot");
                            return false;
                        }
                    }
                }

                foreach (var item in offer.Items.MyOfferedItems.Assets)
                {
                    tradeUsedAssets.Add(new TradeNetwork.Asset { assetID = item.AssetId, appid = Convert.ToInt32(item.AppId) });
                    break;
                }

            }
            Log.Info("Trade Offer Initialized");
            return true;
        }

        public string SendOffer(TradeOffer offer, string offerToken, string securityToken = "")
        {
            string offerID = "";
            string offermessage = "";

            if (!String.IsNullOrEmpty(securityToken))
            {
                offermessage = "Security token: " + securityToken;
            }

            Retry(() => offer.SendWithToken(out offerID, offerToken, offermessage));

            return offerID;
        }

        public void AcceptMobileConfirmation(string offerID)
        {
            TradeOffer offer = null;
            Bot.AcceptAllMobileTradeConfirmations();
            int retries = 0;
            while (true)
            {
                if (!InfinteRetry(() => Bot.TryGetTradeOffer(offerID, out offer))) { return; }

                if (offer.OfferState == TradeOfferState.TradeOfferStateNeedsConfirmation)
                {
                    Bot.AcceptAllMobileTradeConfirmations();
                    if (retries == 3)
                    {
                        Retry(() => offer.Cancel());
                        return;
                    }
                }
                else
                {
                    break;
                }

                retries++;
                Thread.Sleep(5000);
            }
        }


        public int TradeOfferStatus(string offerID, out string statusComment, out string staffComment, out string tradeId, int pollinterval = 5000, int retrylimit = 24)
        {
            statusComment = "";
            staffComment = "";
            tradeId = "";
            TradeOffer tradeoffer = null;

            if (string.IsNullOrWhiteSpace(offerID)) { return 9; }

            try
            {
                for (int retry = 0; retry < retrylimit; retry++)
                {

                    if (!Retry(() => Bot.TryGetTradeOffer(offerID, out tradeoffer)))
                    {
                        if (retry >= retrylimit - 3)
                        {
                            if (!InfinteRetry(() => Bot.TryGetTradeOffer(offerID, out tradeoffer)))
                            {
                                return 9;
                            }
                        }
                        else
                        {
                            Thread.Sleep(pollinterval);
                            continue;
                        }
                    }

                    if (tradeoffer.OfferState == TradeOfferState.TradeOfferStateActive)
                    {
                        if (retry >= retrylimit - 3)
                        {
                            if (!Retry(() => tradeoffer.Cancel()))
                            {
                                if (retry >= retrylimit - 1)
                                {
                                    Log.Info("Unable to cancel trade offer");
                                    statusComment = "User Timed Out";
                                    staffComment = "Unable to cancel offer";
                                    return 9;
                                }
                            }
                            else
                            {
                                Log.Info("User Timed Out");
                                statusComment = "User Time Out";
                                return 4;
                            }
                        }
                        Log.Info("Trade Active");
                    }
                    else if (tradeoffer.OfferState == TradeOfferState.TradeOfferStateNeedsConfirmation)
                    {
                        Retry(() => tradeoffer.Cancel());
                        return 5;
                    }
                    else if (tradeoffer.OfferState == TradeOfferState.TradeOfferStateAccepted)
                    {
                        Log.Info("Offer accepted");
                        tradeId = tradeoffer.TradeId;
                        return 2;
                    }
                    else if (tradeoffer.OfferState == TradeOfferState.TradeOfferStateCanceled)
                    {
                        return 5;
                    }
                    else if (tradeoffer.OfferState == TradeOfferState.TradeOfferStateInEscrow)
                    {
                        Log.Info("Offer Escrow");
                        statusComment = "Offer In Escrow";
                        staffComment = "Exploit attempt, do not entertain";
                        return 9;
                    }
                    else if (tradeoffer.OfferState == TradeOfferState.TradeOfferStateCountered)
                    {
                        Log.Info("Offer Countered");
                        statusComment = "Offer Countered";
                        staffComment = "Exploit attempt, do not entertain";
                        return 9;
                    }
                    else if (tradeoffer.OfferState == TradeOfferState.TradeOfferStateUnknown)
                    {
                        Log.Info("Unknown offer status");
                        staffComment = "Unknown Offer Status";
                        return 9;
                    }
                    else
                    {
                        Log.Info("User Canceled");
                        statusComment = "Offer Invalid/Declined";
                        return 4;
                    }

                    Thread.Sleep(pollinterval);
                }
            }
            catch (Exception e)
            {
                Log.Error(e.ToString());
                staffComment = "Unknown Error, Check Log";
                return 9;
            }

            // We should never reach here
            Log.Warn("Unreachable trade state reached");
            staffComment = "Unreachable state reached";
            return 9;
        }

        public bool InfinteRetry(Func<bool> action, int pollingIntervial = 5000)
        {
            int retries = 3;
            int unauthorised = 3;

            while (true)
            {
                try
                {
                    if (action())
                    {
                        return true;
                    }
                }
                catch (WebException e)
                {

                    int status = (int)((HttpWebResponse)e.Response).StatusCode;

                    if (status == 403 || status == 401)//status == 500
                    {
                        unauthorised--;

                        if (unauthorised == 0)
                        {
                            CookieCheck();
                            unauthorised = 3;
                            retries--;
                        }
                    }

                    Log.Warn("Status Code " + status);

                }
                catch (Exception e)
                {
                    retries--;
                    Log.Error(e.ToString());
                }

                if (retries <= 0) { return false; }

                Thread.Sleep(pollingIntervial);
            }
        }

        public bool Retry(Func<bool> action)
        {
            int retries = 4;
            int unauthorised = 3;

            while (true)
            {
                try
                {
                    if (action())
                    {
                        return true;
                    }
                }
                catch (WebException e)
                {

                    int status = (int)((HttpWebResponse)e.Response).StatusCode;

                    if (status == 403 || status == 401)//status == 500
                    {
                        unauthorised--;

                        if (unauthorised == 0)
                        {
                            CookieCheck();
                            unauthorised = 3;
                        }
                    }
                    else if (status == 500) // Wrong trade offer id or Busy server
                    {
                        retries--;
                    }

                    Log.Warn("Status Code " + status);

                }
                catch (Exception e)
                {
                    Log.Error(e.ToString());
                }

                if (retries <= 0) { return false; }
                retries--;

                Thread.Sleep(2000);
            }
        }
        /*
        public object login = new object();
        public bool isReloging = false;

        public void ReLogin()
        {
            if (Monitor.TryEnter(login))
            {
                isReloging = true;

                try
                {
                    if (Bot.CheckCookies())
                    {
                        Bot.Relogin();
                        while (Bot.IsLoggedIn) { Log.Info("Logining in, Not Started"); Thread.Sleep(1000); }
                        while (!Bot.IsLoggedIn) { Log.Info("Logining in, Not Log in"); Thread.Sleep(1000); }
                    }

                }
                catch (Exception e)
                {
                    Log.Error(e.ToString());
                }
                finally
                {
                    Monitor.Exit(login);
                    isReloging = false;
                    Log.Success("ReLogin Complete");
                }

            }
            else
            {
                do { Thread.Sleep(1000); } while (isReloging);
                Log.Success("Waited Login Complete");
            }
        }
        */

        public List<TradeNetwork.ReceiptItem> GetItemReceipt(string tradeId, out bool sysRequired)
        {
            sysRequired = false;
            for (int i = 0; i < 3; i++)
            {
                try
                {

                    var resp = SteamWeb.Fetch("https://steamcommunity.com/trade/" + tradeId + "/receipt", "GET", null, false);

                    if (string.IsNullOrWhiteSpace(resp))
                    {
                        Thread.Sleep(2000);
                        continue;
                    }

                    //not logged or not our tradeid
                    if (Regex.Match(resp, "{\"success\":false}", RegexOptions.IgnoreCase).Success)
                    {
                        Bot.CheckCookies();
                        continue;
                    }


                    //no new items
                    if (Regex.Match(resp, "No new items acquired", RegexOptions.IgnoreCase).Success)
                    {
                        Log.Error("Empty Offer");
                        break;
                    }

                    /*
                    try
                    {
                        var items = Regex.Matches(resp, @"oItem(?:[\s=]+)(.+?(?=;))", RegexOptions.IgnoreCase | RegexOptions.Singleline);

                        foreach (Match iM in items)
                        {
                            if (!iM.Success)
                            {
                                Log.Error("Regex Error");
                                break;
                            }

                            var g = iM.Groups[1];

                            if (!g.Success)
                            {
                                Log.Error("Regex Error");
                                break;
                            }

                            receiptList.Add(JsonConvert.DeserializeObject<ReceiptItem>(iM.Groups[1].Value));
                        }
                    }
                    catch
                    {

                    }
                    */


                    //get oItem from response
                    var items = Regex.Matches(resp, @"oItem(?:[\s=]+)(?<jsonItem>[^;]*)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

                    List<TradeNetwork.ReceiptItem> receiptList = new List<TradeNetwork.ReceiptItem>();
                    bool error = false;
                    foreach (Match iM in items)
                    {
                        if (!iM.Success)
                        {
                            Log.Error("Regex Error");
                            error = true;
                            break;
                        }

                        var g = iM.Groups["jsonItem"];

                        if (!g.Success)
                        {
                            Log.Error("Regex Error");
                            error = true;
                            break;
                        }
                        try
                        {
                            receiptList.Add(JsonConvert.DeserializeObject<TradeNetwork.ReceiptItem>(g.Value));
                        }
                        catch (Exception e)
                        {
                            sysRequired = true;
                            Log.Info("Regex Deserialisation Error"); // Throw exception there nothing we can do about this because json formating error
                        }

                    }

                    if (error) { continue; }

                    return receiptList;
                }
                catch (Exception e)
                {
                    Log.Warn("Regex Error" + " Trade ID: '" + tradeId + "' " + e.ToString());
                }
            }

            sysRequired = true;
            return null;
        }

        public Dictionary<int, List<TradeNetwork.ItemDetails>> ParseAssetIdWReceipt(string tradeId, Dictionary<int, List<TradeNetwork.ItemDetails>> receiverItems, out bool sysRequired)
        {
            try
            {
                List<TradeNetwork.ReceiptItem> receiptList = GetItemReceipt(tradeId, out sysRequired);

                if (receiptList == null || receiptList.Count == 0) { return null; }

                foreach (int appid in receiverItems.Select(x => x.Key).ToList())
                {
                    List<long> usedAssets = new List<long>();
                    bool inventoryFetched = false;
                    GenericInventory myInventory = null;

                    for (int i = 0; i < receiverItems[appid].Count; i++) // Loop with for.
                    {
                        var rIArray = receiverItems[appid][i];
                        receiverItems[appid][i].assetid = 0;

                        foreach (var rLArray in receiptList) // Loop with for.
                        {
                            if (rIArray.name == rLArray.market_hash_name && appid == rLArray.appid && !usedAssets.Contains(rLArray.assetid))
                            {
                                usedAssets.Add(rLArray.assetid);
                                receiverItems[appid][i].assetid = rLArray.assetid;
                                if (!rLArray.commodity)
                                {
                                    if (!inventoryFetched)
                                    {
                                        myInventory = Bot.FetchInventory(Bot.SteamClient.SteamID, appid);
                                        inventoryFetched = true;
                                    }
                                    string inspectLink = "";
                                    string description = "";

                                    if (myInventory != null)
                                    {
                                        try
                                        {
                                            description = TradeNet.CreateItemDescription(myInventory.Description[rLArray.classid + "_" + rLArray.instanceid], rLArray.appid, out inspectLink);
                                        }
                                        catch
                                        {
                                            sysRequired = true;
                                            description = TradeNet.CreateItemDescription(rLArray, rLArray.appid, out inspectLink);
                                        }
                                    }
                                    else
                                    {
                                        description = TradeNet.CreateItemDescription(rLArray, rLArray.appid, out inspectLink);
                                    }

                                    receiverItems[appid][i].completeDescription = TradeNet.AddInspectButton(description, inspectLink, rLArray.assetid);

                                }
                                break;
                            }
                        }
                    }
                }
                return receiverItems;
            }
            catch (Exception e)
            {
                Log.Error("Parse receipt error" + e.ToString());
                sysRequired = true;
                return null;
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
