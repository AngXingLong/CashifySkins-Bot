using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MySql.Data.MySqlClient;
using System.Timers;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;


namespace SteamTrade
{
    public class TradeNetwork
    {
        private const string myConnection = "datasource=localhost;port=3306;database=cashifyskins;username=sys-app;password=s2pjh%!151231@%88B;Charset=utf8;";

        public ulong botsid;

        public TradeNetwork(ulong botsid)
        {
            this.botsid = botsid;
        }

        public class TradeInfo
        {
            public string offerID { get; set; }

            public long transactionID { get; set; }

            public int tradeType { get; set; }

            public string securityToken { get; set; }

            public string offerToken { get; set; }

            public int creditDifference { get; set; }

            public Dictionary<int, List<ItemDetails>> senderItems { get; set; }

            public Dictionary<int, List<ItemDetails>> receiverItems { get; set; }

            public ulong receiver { get; set; }

            public List<Asset> usedAssets { get; set; }

            public int retries { get; set; }

        }

        public class Asset
        {
            public long assetID { get; set; }
            public int appid { get; set; }
        }

        public class ItemDetails
        {
            public string name { get; set; }
            public int quantity { get; set; }
            public long assetid { get; set; }

            public int appid { get; set; }
            public long id { get; set; }
            public int itemid { get; set; }
            public double price { get; set; }
            public double sellerReceive { get; set; }
            public ulong steamid { get; set; }
            public string completeDescription { get; set; }
            public long itemTransactionId { get; set; }

        }

        public class ReceiptItem : GenericInventory.RgDescriptions
        {
            [JsonProperty("id")]
            public long assetid { get; set; }

            [JsonProperty("item_id")]
            public long itemid { get; set; }
        }

        private class SyncDetails
        {
            public long transactionId { get; set; }
            public int itemId { get; set; }
            public long assetid { get; set; }
        }

        public void ReportState()
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                MySqlTransaction myTran = con.BeginTransaction();
                MySqlCommand cmd = new MySqlCommand("update bot set last_reported = now(), status = 1 where steamid = @botsid", con);
                cmd.Parameters.AddWithValue("@botsid", botsid);
                cmd.ExecuteNonQuery();
                myTran.Commit();
            }
        }

        public bool CheckSyncRequired()
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();

                MySqlCommand cmd = new MySqlCommand("select count(*) from item_transaction where status = 1 and botsid = @botsid and (description = '' or assetid = 0) limit 1", con);
                cmd.Parameters.AddWithValue("@botsid", botsid);

                MySqlDataReader reader = cmd.ExecuteReader();

                int count = 0;
                while (reader.Read())
                {
                    count = reader.GetInt32(0);
                }

                reader.Close();

                if (count != 0)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        public void UpdateInventoryAssetId(SteamTrade.GenericInventory inventory, int appid, bool syncDescription = false)
        {
            List<ReceiptItem> assetList = new List<ReceiptItem>();

            foreach (var dItem in inventory.Description)
            {
                foreach (var iItem in inventory.Inventory)
                {
                    string iIdentifer = string.Format("{0}_{1}", iItem.Value.classid, iItem.Value.instanceid);
                    if (dItem.Key == iIdentifer)
                    {
                        assetList.Add(new ReceiptItem { market_hash_name = dItem.Value.market_hash_name, assetid = iItem.Key });
                    }
                }
            }

            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                MySqlTransaction myTran = con.BeginTransaction();

                MySqlCommand cmd = new MySqlCommand("delete i from inventory i join pricelist p on i.item_id = p.id where i.botsid = @botsid and p.appid = @appid;", con);
                cmd.Parameters.AddWithValue("@botsid", botsid);
                cmd.Parameters.AddWithValue("@appid", appid);

                foreach (var item in assetList)
                {
                    cmd.Parameters["@appid"].Value = appid;
                    cmd.ExecuteNonQuery();
                }

                cmd.CommandText = "INSERT IGNORE INTO inventory(item_id, botsid, assetid) VALUES (IFNULL((select id from pricelist where name = @name and appid = @appid),1), @botsid, @assetid) ;";

                cmd.Parameters.AddWithValue("@name", "");
                cmd.Parameters.AddWithValue("@assetid", "");

                foreach (var item in assetList)
                {
                    cmd.Parameters["@name"].Value = item.market_hash_name;
                    cmd.Parameters["@assetid"].Value = item.assetid;
                    cmd.ExecuteNonQuery();
                }

                List<SyncDetails> incorrectList = new List<SyncDetails>();
                List<SyncDetails> surplusAssetsId = new List<SyncDetails>();
                List<SyncDetails> correctedList = new List<SyncDetails>();

                cmd.CommandText = "select id, item_id from item_transaction it where status in (1,10) and botsid = @botsid and NOT EXISTS (SELECT null FROM inventory i WHERE i.assetid = it.assetid and i.item_id = it.item_id and i.botsid = it.botsid) ;";
                MySqlDataReader reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    incorrectList.Add(new SyncDetails { transactionId = reader.GetInt64(0), itemId = reader.GetInt32(1) });
                }

                reader.Close();

                if (incorrectList.Count == 0)
                {
                    goto end;
                }

                cmd.CommandText = "select assetid, item_id from inventory i where botsid = @botsid and NOT EXISTS (SELECT null FROM item_transaction it WHERE i.assetid = it.assetid and i.item_id = it.item_id and i.botsid = it.botsid and it.status in (1,3,8,10,11)); ";
                reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    surplusAssetsId.Add(new SyncDetails { assetid = reader.GetInt64(0), itemId = reader.GetInt32(1) });
                }

                reader.Close();

                if (surplusAssetsId.Count == 0)
                {
                    goto end;
                }

                foreach (var il in incorrectList)
                {
                    SyncDetails indexRemove = null;

                    foreach (var sAI in surplusAssetsId)
                    {
                        if (il.itemId == sAI.itemId) // Chances are higher that the assetid will be reassigned back to it's the origial assetid if assetid is assigned from its origin
                        {
                            indexRemove = sAI;
                            correctedList.Add(new SyncDetails { transactionId = il.transactionId, assetid = sAI.assetid });
                            break;
                        }
                    }

                    if (indexRemove != null) { surplusAssetsId.Remove(indexRemove); continue; }

                }

                cmd.CommandText = "update item_transaction set assetid = @assetid, botsid = @botsid where id = @transaction_id ";
                cmd.Prepare();

                cmd.Parameters.AddWithValue("@transaction_id", "");

                foreach (var item in correctedList)
                {
                    cmd.Parameters["@assetid"].Value = item.assetid;
                    cmd.Parameters["@transaction_id"].Value = item.transactionId;
                    cmd.ExecuteNonQuery();
                }

                end:
                myTran.Commit();
                con.Close();
                if (correctedList.Count > 0) { syncDescription = true; }
            }

            if (syncDescription) { UpdateItemDescriptions(inventory, appid); }

        }
        //http://www.neowin.net/forum/topic/994146-c-help-php-compatible-string-gzip/
        public static string Compress(string text)
        {

            byte[] buffer = Encoding.UTF8.GetBytes(text);

            var memoryStream = new MemoryStream();
            using (GZipStream gZipStream = new GZipStream(memoryStream, CompressionLevel.Fastest, true))
            {
                gZipStream.Write(buffer, 0, buffer.Length);
            }

            memoryStream.Position = 0;

            var compressedData = new byte[memoryStream.Length];
            memoryStream.Read(compressedData, 0, compressedData.Length);

            return Convert.ToBase64String(compressedData);
        }

        public string AddInspectButton(string description, string inpectLink, long assetid)
        {
            if (inpectLink != "")
            {
                inpectLink = inpectLink.Replace("%owner_steamid%", Convert.ToString(botsid));
                inpectLink = inpectLink.Replace("%assetid%", Convert.ToString(assetid));
                description += "<a class='inspect' href='" + inpectLink + "'>Inspect in Game</a>";
            }

            return Compress(description);
        }

        public string CreateItemDescription(GenericInventory.RgDescriptions itemDescription, int appid, out string inspectLink)
        {
            string appName = "";
            string appIconLink = "";
            inspectLink = "";
            if (appid == 753)
            {
                appName = "Steam";
                appIconLink = "/images/apps/icons/753.jpg";
            }
            else if (appid == 730)
            {
                appName = "Counter-Strike: Global Offensive";
                appIconLink = "/images/apps/icons/730.jpg";
            }
            else if (appid == 570)
            {
                appName = "Dota 2";
                appIconLink = "/images/apps/icons/570.jpg";
            }
            else if (appid == 440)
            {
                appName = "Team Fortress 2";
                appIconLink = "/images/apps/icons/440.jpg";
            }

            string description = "";

            if (!itemDescription.commodity)
            {
                string colorHeader = !String.IsNullOrEmpty(itemDescription.name_color) ? "style='color:#" + itemDescription.name_color + ";'" : "";

                string color = "";
                string icon_url = string.IsNullOrEmpty(itemDescription.icon_url_large) ? itemDescription.icon_url : itemDescription.icon_url_large;

                icon_url = "https://steamcommunity-a.akamaihd.net/economy/image/" + icon_url;

                bool closed = true;

                description += "<img src='" + icon_url + "' class='notification_product_display_img'>";
                description += "<h2 " + colorHeader + ">" + itemDescription.market_hash_name + "</h2>";
                description += "<div class='notification_product_display_type'><img src='" + appIconLink + "'>" + itemDescription.type + "<br>" + appName + "</div><br>";

                if (itemDescription.descriptions != null)
                {
                    foreach (var items in itemDescription.descriptions)
                    {
                        if (items.color != null)
                        {
                            if (items.color != color && !closed)
                            {
                                description += "</span>";
                                closed = true;
                            }

                            if (closed)
                            {
                                color = items.color;
                                description += "<span style='color:#" + color + ";'>";
                                closed = false;
                            }

                        }
                        else if (!closed && color != "")
                        {
                            description += "</span>";
                            closed = true;
                            color = "";
                        }

                        description += items.value + "<br>";

                    }

                }
                if (!closed)
                {
                    description += "</span>";
                }

                if (itemDescription.market_actions != null)
                {
                    foreach (var item in itemDescription.actions)
                    {
                        string buttonName = item.name;
                        if (buttonName.Contains("Inspect"))
                        {
                            inspectLink = item.link;
                            break;
                        }
                    }
                }
            }

            return description;

        }

        public void UpdateItemDescriptions(SteamTrade.GenericInventory inventory, int appid)
        {
            List<ReceiptItem> assetList = new List<ReceiptItem>();

            foreach (var dItem in inventory.Description)
            {
                String inspectLink = "";
                string description = CreateItemDescription(dItem.Value, appid, out inspectLink);
                string dIdentifer = dItem.Key;

                foreach (var iItem in inventory.Inventory)
                {
                    string iIdentifer = string.Format("{0}_{1}", iItem.Value.classid, iItem.Value.instanceid);
                    if (dIdentifer == iIdentifer)
                    {
                        description = AddInspectButton(description, inspectLink, iItem.Key);
                        assetList.Add(new ReceiptItem { market_hash_name = dItem.Value.market_hash_name, assetid = iItem.Key, completeDescription = description });
                    }
                }

            }

            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                MySqlTransaction myTran = con.BeginTransaction();

                MySqlCommand cmd = new MySqlCommand("update item_transaction set description = @description where status in (1,3,8) and botsid = @botsid and assetid = @assetid and item_id = IFNULL((select id from pricelist where name = @name and appid = @appid),0) ;", con);
                cmd.Prepare();
                cmd.Parameters.AddWithValue("@botsid", botsid);
                cmd.Parameters.AddWithValue("@appid", appid);
                cmd.Parameters.AddWithValue("@name", "");
                cmd.Parameters.AddWithValue("@assetid", "");
                cmd.Parameters.AddWithValue("@description", "");

                foreach (var item in assetList)
                {
                    cmd.Parameters["@name"].Value = item.market_hash_name;
                    cmd.Parameters["@assetid"].Value = item.assetid;
                    cmd.Parameters["@description"].Value = item.completeDescription;
                    cmd.ExecuteNonQuery();
                }

                myTran.Commit();
                con.Close();
            }
        }


        public bool SyncListingAssetId() // returns true if description need sync
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                MySqlTransaction myTran = con.BeginTransaction();

                List<SyncDetails> incorrectList = new List<SyncDetails>();
                List<SyncDetails> surplusAssetsId = new List<SyncDetails>();
                List<SyncDetails> correctedList = new List<SyncDetails>();

                MySqlCommand cmd = new MySqlCommand("select id, item_id from item_transaction it where status in (1,10) and botsid = @botsid and NOT EXISTS (SELECT null FROM inventory i WHERE i.assetid = it.assetid and i.item_id = it.item_id and i.botsid = it.botsid) ;", con);
                cmd.Parameters.AddWithValue("@botsid", botsid);
                MySqlDataReader reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    incorrectList.Add(new SyncDetails { transactionId = reader.GetInt64(0), itemId = reader.GetInt32(1) });
                }

                reader.Close();

                if (incorrectList.Count == 0)
                {
                    goto end;
                }

                cmd.CommandText = "select assetid, item_id from inventory i where botsid = @botsid and NOT EXISTS (SELECT null FROM item_transaction it WHERE i.assetid = it.assetid and i.item_id = it.item_id and i.botsid = it.botsid and it.status in (1,3,8,10,11)); ";
                reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    surplusAssetsId.Add(new SyncDetails { assetid = reader.GetInt64(0), itemId = reader.GetInt32(1) });
                }

                reader.Close();

                if (surplusAssetsId.Count == 0)
                {
                    goto end;
                }

                foreach (var il in incorrectList)
                {
                    SyncDetails indexRemove = null;

                    foreach (var sAI in surplusAssetsId)
                    {
                        if (il.itemId == sAI.itemId) // Chances are higher that the assetid will be reassigned back to it's the origial assetid if assetid is assigned from its origin
                        {
                            indexRemove = sAI;
                            correctedList.Add(new SyncDetails { transactionId = il.transactionId, assetid = sAI.assetid });
                            break;
                        }
                    }

                    if (indexRemove != null) { surplusAssetsId.Remove(indexRemove); continue; }

                }

                cmd.CommandText = "update item_transaction set assetid = @assetid, botsid = @botsid where id = @transaction_id ";
                cmd.Prepare();

                cmd.Parameters.AddWithValue("@transaction_id", "");
                cmd.Parameters.AddWithValue("@assetid", "");

                foreach (var item in correctedList)
                {
                    cmd.Parameters["@assetid"].Value = item.assetid;
                    cmd.Parameters["@transaction_id"].Value = item.transactionId;
                    cmd.ExecuteNonQuery();
                }

                end:
                //cmd.CommandText = "UNLOCK TABLES";
                //cmd.ExecuteNonQuery();
                myTran.Commit();
                if (correctedList != null) { return true; }
                return false;
            }
        }

        public void ManualInventoryUpdate(List<ReceiptItem> assetIdList, bool process)
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                MySqlTransaction myTran = con.BeginTransaction();
                if (!process)
                {
                    MySqlCommand cmd = new MySqlCommand("delete from inventory where botsid = @botsid and assetid = @assetid and item_id = @item_id;", con);
                    cmd.Prepare();
                    cmd.Parameters.AddWithValue("@botsid", botsid);
                    cmd.Parameters.AddWithValue("@item_id", "");
                    cmd.Parameters.AddWithValue("@assetid", "");

                    foreach (var appid in assetIdList)
                    {
                        cmd.Parameters["@item_id"].Value = appid.itemid;
                        cmd.Parameters["@assetid"].Value = appid.assetid;
                        cmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    MySqlCommand cmd = new MySqlCommand("INSERT IGNORE INTO inventory(item_id, botsid, assetid, description) VALUES (@item_id, @botsid, @assetid, @description) ;", con);
                    cmd.Prepare();

                    cmd.Parameters.AddWithValue("@botsid", botsid);
                    cmd.Parameters.AddWithValue("@item_id", 0);
                    cmd.Parameters.AddWithValue("@assetid", 0);
                    cmd.Parameters.AddWithValue("@description", "");

                    foreach (var item in assetIdList)
                    {
                        cmd.Parameters["@item_id"].Value = item.itemid;
                        cmd.Parameters["@assetid"].Value = item.assetid;
                        cmd.Parameters["@description"].Value = item.completeDescription;
                        cmd.ExecuteNonQuery();
                    }

                }

                myTran.Commit();
                con.Close();
            }

        }

        public List<TradeInfo> GetIncompleteTrade()
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();

                MySqlCommand cmd = new MySqlCommand("select id, offer_id, type from trade_transaction where status = 2 and botsid = @botsid;", con);
                cmd.Parameters.AddWithValue("@botsid", botsid);

                MySqlDataReader reader = cmd.ExecuteReader();
                List<TradeInfo> transactionList = new List<TradeInfo>();
                Dictionary<int, List<TradeNetwork.ItemDetails>> itemList = new Dictionary<int, List<TradeNetwork.ItemDetails>>();

                while (reader.Read())
                {
                    transactionList.Add(new TradeInfo { transactionID = reader.GetInt64(0), offerID = reader.GetString(1), tradeType = reader.GetInt32(2) });
                }

                reader.Close();

                return transactionList;
            }
        }

        public void RemoveProccesingTrade()
        {
            List<TradeInfo> transactionList = new List<TradeInfo>();

            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();

                MySqlCommand cmd = new MySqlCommand("select id, type from trade_transaction where status in (0,1) and botsid = @botsid;", con);
                cmd.Parameters.AddWithValue("@botsid", botsid);

                MySqlDataReader reader = cmd.ExecuteReader();

                Dictionary<int, List<TradeNetwork.ItemDetails>> itemList = new Dictionary<int, List<TradeNetwork.ItemDetails>>();

                while (reader.Read())
                {
                    transactionList.Add(new TradeInfo { transactionID = reader.GetInt64(0), tradeType = reader.GetInt32(1) });
                }

                reader.Close();

            }

            foreach (var item in transactionList)
            {
                TradeFailure(item.transactionID, item.tradeType, 5, "", "Bot Restart");
            }
        }

        public bool CheckValidTransaction(long transactionid)
        {
            try
            {
                using (MySqlConnection con = new MySqlConnection(myConnection))
                {
                    con.Open();
                    MySqlTransaction myTran = con.BeginTransaction();
                    MySqlCommand cmd = new MySqlCommand("update trade_transaction set status = 1 where id = @id and status = 0 and time_start > DATE_SUB(now(),INTERVAL 30 MINUTE) limit 1;", con);
                    cmd.Parameters.AddWithValue("@id", transactionid);
                    myTran.Commit();
                    if (cmd.ExecuteNonQuery() > 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            return false;
        }

        public TradeInfo GetTradeInfo(long transactionid)
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                MySqlCommand cmd = new MySqlCommand("select t.usersid, t.type, t.security_token, u.offer_token from trade_transaction t join user u on u.steamid = t.usersid WHERE t.id = @id;", con);
                cmd.Parameters.AddWithValue("@id", transactionid);

                MySqlDataReader reader = cmd.ExecuteReader();
                TradeInfo transaction = new TradeInfo();
                Dictionary<int, List<TradeNetwork.ItemDetails>> itemList = new Dictionary<int, List<TradeNetwork.ItemDetails>>();

                while (reader.Read())
                {
                    transaction.receiver = reader.GetUInt64(0);
                    transaction.tradeType = reader.GetInt32(1);
                    transaction.securityToken = reader.GetString(2);
                    transaction.offerToken = reader.GetString(3);
                }

                reader.Close();
                if (transaction.tradeType == 0)
                {
                    cmd.CommandText = "SELECT pl.id, pl.name, it.assetid, pl.appid, it.id FROM trade_transaction_details ttd inner join item_transaction it on ttd.item_transaction_id = it.id inner join pricelist pl on it.item_id = pl.id WHERE ttd.trade_id = @id;";

                    reader = cmd.ExecuteReader();

                    while (reader.Read())
                    {
                        int appid = reader.GetInt32(3);

                        if (!itemList.ContainsKey(appid))
                        {
                            itemList.Add(appid, new List<TradeNetwork.ItemDetails>());
                        }

                        itemList[appid].Add(new TradeNetwork.ItemDetails { itemid = reader.GetInt32(0), name = reader.GetString(1), assetid = reader.GetInt64(2), itemTransactionId = reader.GetInt64(4) });
                    }
                }
                else
                {
                    cmd.CommandText = "SELECT pl.appid, it.assetid FROM trade_transaction_details ttd inner join item_transaction it on ttd.item_transaction_id = it.id inner join pricelist pl on it.item_id = pl.id WHERE ttd.trade_id = @id;";

                    reader = cmd.ExecuteReader();

                    while (reader.Read())
                    {
                        int appid = reader.GetInt32(0);

                        if (!itemList.ContainsKey(appid))
                        {
                            itemList.Add(appid, new List<TradeNetwork.ItemDetails>());
                        }

                        itemList[appid].Add(new TradeNetwork.ItemDetails { assetid = reader.GetInt64(1) });
                    }
                }

                reader.Close();

                transaction.transactionID = transactionid;

                if (transaction.tradeType == 0) { transaction.receiverItems = itemList; }
                else { transaction.senderItems = itemList; }

                return transaction;
            }
        }

        public void UpdateOfferStatus(long transactionid, int status, string statusComment, string staffComment, string offerid = "")
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                MySqlTransaction myTran = con.BeginTransaction();
                MySqlCommand cmd = new MySqlCommand("update trade_transaction set status = @status, offer_id = @offer_id, status_comment = @status_comment, staff_comment = @staff_comment  where id = @id;", con);
                cmd.Parameters.AddWithValue("@id", transactionid);
                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@offer_id", offerid);
                cmd.Parameters.AddWithValue("@status_comment", statusComment);
                cmd.Parameters.AddWithValue("@staff_comment", staffComment);
                cmd.ExecuteNonQuery();
                myTran.Commit();
            }
        }

        public void TradeFailure(long transactionID, int type, int status, string statusComment, string staffComment)
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                MySqlTransaction myTran = con.BeginTransaction();

                MySqlCommand cmd = new MySqlCommand("update trade_transaction set status = @status, status_comment = @status_comment, staff_comment = @staff_comment, time_end = now() where id = @id;", con);
                cmd.Parameters.AddWithValue("@id", transactionID);
                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@status_comment", statusComment);
                cmd.Parameters.AddWithValue("@staff_comment", staffComment);
                cmd.ExecuteNonQuery();

                if (type == 0)
                {
                    cmd.CommandText = "update trade_transaction_details ttd inner join item_transaction it on it.id = ttd.item_transaction_id set it.status = 6 where ttd.trade_id = @id";
                }
                else if (type == 1) //required
                {
                    cmd.CommandText = "update trade_transaction_details ttd inner join item_transaction it on it.id = ttd.item_transaction_id set it.status = 1 where ttd.trade_id = @id";
                }
                else if (type == 2)
                {
                    cmd.CommandText = "update trade_transaction_details ttd inner join item_transaction it on it.id = ttd.item_transaction_id set it.status = 10 where ttd.trade_id = @id";
                }
                else if (type == 3) //required
                {
                    cmd.CommandText = "update trade_transaction_details ttd inner join item_transaction it on it.id = ttd.item_transaction_id set it.status = 1, it.buyer_sid = 0 where ttd.trade_id = @id";
                }

                cmd.ExecuteNonQuery();
                myTran.Commit();
            }

            if (type == 1 || type == 3)
            {
                MatchBuyOrder(transactionID);
            }

        }

        public class SaleDetails
        {
            public long saleId { get; set; }
            public int itemId { get; set; }
        }

        public void TradeSuccess(long transactionID, int tradeType, string statusComment, string staffComment, Dictionary<int, List<TradeNetwork.ItemDetails>> receiverItems = null)
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                MySqlTransaction myTran = con.BeginTransaction();

                MySqlCommand cmd = new MySqlCommand("update trade_transaction set status = 3, status_comment = @status_comment, staff_comment = @staff_comment, time_end = now() where id = @id ;", con);

                cmd.Parameters.AddWithValue("@id", transactionID);
                cmd.Parameters.AddWithValue("@status_comment", "");
                cmd.Parameters.AddWithValue("@staff_comment", staffComment);
                cmd.ExecuteNonQuery();

                if (tradeType == 0)
                {
                    cmd.Parameters.AddWithValue("@botsid", botsid);
                    cmd.Parameters.AddWithValue("@assetid", "");
                    cmd.Parameters.AddWithValue("@item_id", "");
                    cmd.Parameters.AddWithValue("@sale_id", "");
                    cmd.Parameters.AddWithValue("@description", "");

                    if (receiverItems != null)
                    {
                        cmd.CommandText = "update item_transaction set status = 1, assetid = @assetid, botsid = @botsid, time_in = now(), description = @description where id = @sale_id and item_id = @item_id;";
                        foreach (var item in receiverItems)
                        {
                            foreach (var item2 in item.Value)
                            {
                                cmd.Parameters["@item_id"].Value = item2.itemid;
                                cmd.Parameters["@sale_id"].Value = item2.itemTransactionId;
                                cmd.Parameters["@assetid"].Value = item2.assetid;
                                cmd.Parameters["@description"].Value = item2.completeDescription;
                                cmd.ExecuteNonQuery();
                            }
                        }

                        cmd.CommandText = "INSERT INTO inventory (item_id, botsid, assetid) values (@item_id,@botsid,@assetid);";

                        foreach (var item in receiverItems)
                        {
                            foreach (var item2 in item.Value)
                            {
                                cmd.Parameters["@item_id"].Value = item2.itemid;
                                cmd.Parameters["@assetid"].Value = item2.assetid;

                                if (item2.assetid != 0)
                                {
                                    cmd.ExecuteNonQuery();
                                }

                            }
                        }


                    }

                }
                else if (tradeType == 1)
                {
                    cmd.CommandText = "update item_transaction it inner join trade_transaction_details ttd on ttd.item_transaction_id = it.id set it.status = 4, it.time_out = now(), it.description = '' where ttd.trade_id = @id;";
                    cmd.ExecuteNonQuery();
                }
                else if (tradeType == 2)
                {
                    cmd.CommandText = "update item_transaction it inner join trade_transaction_details ttd on ttd.item_transaction_id = it.id set it.status = 12, it.time_out = now(), it.description = '' where ttd.trade_id = @id;";
                    cmd.ExecuteNonQuery();
                }
                else if (tradeType == 3)
                {
                    cmd.CommandText = "select it.id from item_transaction it inner join trade_transaction_details ttd on ttd.item_transaction_id = it.id inner join trade_transaction tt on tt.id = ttd.trade_id where tt.id = @id;";
                    cmd.ExecuteNonQuery();

                    MySqlDataReader reader = cmd.ExecuteReader();
                    long itemTransactionId = 0;

                    while (reader.Read())
                    {
                        itemTransactionId = reader.GetInt64(0);
                    }

                    reader.Close();

                    cmd.Parameters.AddWithValue("@item_transaction_id", itemTransactionId);

                    cmd.CommandText = "update item_transaction set status = 12, time_out = now(), description = '', time_transacted = now() where id = @item_transaction_id;";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "update user u inner join item_transaction it on it.seller_sid = u.steamid set u.credit = u.credit + it.seller_receive where it.id = @item_transaction_id;";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "update user u inner join item_transaction it on it.buyer_sid = u.steamid set u.credit = u.credit - it.price where it.id = @item_transaction_id;";
                    cmd.ExecuteNonQuery();
                }

                if (tradeType != 0)
                {
                    cmd.CommandText = "delete i from inventory i inner join item_transaction it on i.item_id = it.item_id and i.botsid = it.botsid and i.assetid = it.assetid inner join trade_transaction_details ttd on ttd.item_transaction_id = it.id where ttd.trade_id = @id; ";
                    cmd.ExecuteNonQuery();
                }

                myTran.Commit();
            }

            if(tradeType == 0)
            {
                MatchBuyOrder(transactionID);
            }
        }
        public class Order
        {
            public long id { get; set; }
            public int itemId { get; set; }
            public double price { get; set; }
            public double sellerReceive { get; set; }
            public int quantity { get; set; }
            public ulong steamid { get; set; }
        }

        public void MatchBuyOrder(long transactionid)
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                
                MySqlCommand cmd = new MySqlCommand("LOCK tables buy_order write, item_transaction write, user write;", con);
                try
                {
                    MySqlTransaction myTran = con.BeginTransaction();
                    cmd.CommandText = "SELECT it.seller_sid FROM trade_transaction_details ttd inner join item_transaction it on ttd.item_transaction_id = it.id WHERE ttd.trade_id = @id and status = 1 limit 1;";
                    cmd.Parameters.AddWithValue("@id", transactionid);
                    MySqlDataReader reader = cmd.ExecuteReader();
                    ulong sellerSID = 0;

                    while (reader.Read())
                    {
                        sellerSID = reader.GetUInt64(0);
                    }

                    reader.Close();

                    if(sellerSID == 0){return;}

                    cmd.CommandText = "SELECT it.id, it.item_id, it.price, it.seller_receive FROM trade_transaction_details ttd inner join item_transaction it on ttd.item_transaction_id = it.id WHERE ttd.trade_id = @id and status = 1;";
                    reader = cmd.ExecuteReader();

                    List<Order> sellOrderList = new List<Order>();
                    while (reader.Read())
                    {
                        sellOrderList.Add(new Order { id = reader.GetInt64(0), itemId = reader.GetInt32(1), price = reader.GetDouble(2), sellerReceive = reader.GetDouble(3) });
                    }

                    reader.Close();
                    double sellerEarnings = 0;
                    foreach (var sItem in sellOrderList)
                    {
                        Order buyOrder = null;
                        cmd.Parameters.Clear();

                        cmd.CommandText = "select id, quantity, price, steamid from buy_order where item_id = @item_id and price >= @price order by id limit 1";
                        cmd.Parameters.AddWithValue("@item_id", sItem.itemId);
                        cmd.Parameters.AddWithValue("@price", sItem.price);

                        reader = cmd.ExecuteReader();

                        while (reader.Read())
                        {
                            buyOrder = new Order { id = reader.GetInt64(0), quantity = reader.GetInt32(1), price = reader.GetDouble(2), steamid = reader.GetUInt64(3) };
                        }
                        reader.Close();

                        if (buyOrder == null) { continue; }

                        cmd.CommandText = "update item_transaction set status = 10, time_transacted = now(), buyer_sid = @buyer_sid where id = @item_transaction_id;";
                        cmd.Parameters.AddWithValue("@buyer_sid", buyOrder.steamid);
                        cmd.Parameters.AddWithValue("@item_transaction_id", sItem.id);
                        cmd.ExecuteNonQuery();

                        // if buyer 
                        if (buyOrder.price > sItem.price)
                        {
                            double refund = buyOrder.price - sItem.price;
                            cmd.CommandText = "update user set credit = credit + @refund where steamid = @buyer_sid;";
                            cmd.Parameters.AddWithValue("@refund", refund);
                            cmd.ExecuteNonQuery();
                        }

                        sellerEarnings += sItem.sellerReceive;
                        buyOrder.quantity--;
 
                        if (buyOrder.quantity == 0)
                        {
                            cmd.CommandText = "delete from buy_order where id = @buy_order_id;";
                            cmd.Parameters.AddWithValue("@buy_order_id", buyOrder.id);
                            cmd.ExecuteNonQuery();
                        }
                        else
                        {
                            cmd.CommandText = "update buy_order set quantity = @buy_order_quantity where id = @buy_order_id;";
                            cmd.Parameters.AddWithValue("@buy_order_quantity", buyOrder.quantity);
                            cmd.Parameters.AddWithValue("@buy_order_id", buyOrder.id);
                            cmd.ExecuteNonQuery();
                        }

                    }

                    if (sellerEarnings > 0)
                    {
                        cmd.CommandText = "update user set credit = credit + @seller_receive where steamid = @seller_sid;";
                        cmd.Parameters.AddWithValue("@seller_receive", sellerEarnings);
                        cmd.Parameters.AddWithValue("@seller_sid", sellerSID);
                        cmd.ExecuteNonQuery();
                    }

                    myTran.Commit();
                }
                catch(Exception e)
                {
                    Console.WriteLine("Order Match error: "+e.ToString());
                }
                cmd.CommandText = "UNLOCK TABLES";
                
            }
        }

        // This section belongs to Bot Manager Redis
        public void UpdateBotStatus(int status, ulong steamid)
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();
                MySqlTransaction myTran = con.BeginTransaction();
                MySqlCommand cmd = new MySqlCommand("update bot set status = @status where steamid = @steamid;", con);
                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@steamid", steamid);
                cmd.ExecuteNonQuery();
                myTran.Commit();

            }
        }

        public List<ulong> BotExepectedStatusList(int expectedStatus)
        {
            using (MySqlConnection con = new MySqlConnection(myConnection))
            {
                con.Open();

                MySqlCommand cmd = new MySqlCommand("select steamid from bot where expected_status = @status;", con);

                cmd.Parameters.AddWithValue("@status", expectedStatus);

                MySqlDataReader reader = cmd.ExecuteReader();

                List<ulong> botSteamIDList = new List<ulong>();

                while (reader.Read())
                {
                    botSteamIDList.Add(reader.GetUInt64(0));
                }

                reader.Close();

                return botSteamIDList;

            }
        }
    }
}
