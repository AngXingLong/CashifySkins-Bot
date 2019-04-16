using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SteamKit2;
using SteamTrade.TradeWebAPI;
using System.Threading;
using System.Net;

namespace SteamTrade
{
    public class GenericInventory
    {
        private readonly SteamWeb steamWeb;
 
        public GenericInventory(SteamWeb steamWeb)
        {
            this.steamWeb = steamWeb;
        }

        public class RootObject
        {
            public bool success { get; set; }

            public string error { get; set; }

            public Dictionary<long, RgInventory> rgInventory { get; set; }

            public Dictionary<string, RgDescriptions> rgDescriptions { get; set; }

            public List<object> rgCurrency { get; set; }
        }

        public class RgInventory
        {
            public long id { get; set; }
            public int classid { get; set; }
            public int instanceid { get; set; }
            public string amount { get; set; }
            public int pos { get; set; }
        }

        public class RgDescriptions
        {
            public int appid { get; set; }
            public string name { get; set; }
            public string type { get; set; }
            public bool tradable { get; set; }
            public bool marketable { get; set; }
            public string url { get; set; }
            public string icon_url_large { get; set; }
            public string icon_url { get; set; }
            public int classid { get; set; }
            public string market_hash_name { get; set; }
            public int instanceid { get; set; }
            public List<MarketAction> actions { get; set; }
            public List<MarketAction> market_actions { get; set; }
            public List<ItemDescription> descriptions { get; set; }
            public string completeDescription { get; set; }
            public string name_color { get; set;}
            public bool commodity { get; set; }
        }
        public class MarketAction
        {
            public string link { get; set; }
            public string name { get; set; }
        }
        public class ItemDescription
        {
            public string type { get; set; }
            public string value { get; set; }
            public string color { get; set; }
        }
        public class PendingItem
        {
            public int Appid { get; set; }
            public int Amount { get; set; }
        }

        public Dictionary<long, RgInventory> Inventory = new Dictionary<long, RgInventory>();
        public Dictionary<string, RgDescriptions> Description = new Dictionary<string, RgDescriptions>();


        public bool FetchInventoryData(int appid, SteamID steamid)
        {

            int contextID = 2;
            if (appid == 753) { contextID = 6; }
            string response = "";

            for (int i = 6; i >= 0; i--)
            {
                try
                {
                    response = steamWeb.Fetch(string.Format("https://steamcommunity.com/profiles/{0}/inventory/json/{1}/{2}/?trading=1",
                    steamid.ConvertToUInt64(), appid, contextID), "GET", null, true);
  
                    RootObject invResponse = JsonConvert.DeserializeObject<RootObject>(response);

                    if (invResponse.success == false)
                    {
                        return false;
                    }
                    else
                    {
                        Inventory = invResponse.rgInventory;
                        Description = invResponse.rgDescriptions;
                        return true;
                    }
                }
                catch (WebException e)
                {
                    int status = (int)((HttpWebResponse)e.Response).StatusCode;

                    if (status == 429)
                    {
                        i -= 2;
                    }
                }
                catch
                {
                    if (!String.IsNullOrEmpty(response))
                    {

                        return false;
                    }
                }
              
                Thread.Sleep(2000);
            }
            return false;
        }


        public class ItemDescriptions
        {
            public string name { get; set; }
            public long assetid { get; set; }
            public string description { get; set; }
        }
     

    }
}