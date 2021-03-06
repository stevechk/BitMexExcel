﻿using fastJSON;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using WebSocket4Net;

namespace BitMex
{
    internal class BitMexAPI
    {
        public event EventHandler<MarketDataSnapshot> OnDepthUpdate;
        public event EventHandler<BitMexTrade> OnTradeUpdate;

        private WebSocket ws;
        private List<string> SnapshotQueue = new List<string>();

        public void Reconnect()
        {
            //connect to public websocket
            ws = new WebSocket("wss://www.bitmex.com/realtime/websocket?heartbeat=true");
            ws.AutoSendPingInterval = 30;  //30 seconds ping
            ws.EnableAutoSendPing = true;

            //callbacks
            ws.MessageReceived += ws_MessageReceived;
            ws.Error += ws_Error;
            ws.Closed += ws_Closed;
            ws.Opened += ws_Opened;
            ws.Open();            
        }

        internal void Close()
        {
            if(ws != null)
                ws.Close();
        }

        private void ws_Opened(object sender, EventArgs e)
        {
            Logging.Log("Websocket opened");
        }

        private void ws_Closed(object sender, EventArgs e)
        {
            Logging.Log("Websocket closed");
        }

        private void ws_Error(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
        {
            Logging.Log("Websocket error {0}", e.Exception.Message);
            Reconnect();
        }

        private void ws_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            //pass to message cracker
            HandleData(e.Message);
        }

        public void GetSnapshot(string product)
        {
            //get snapshot
            if(ws != null && ws.State == WebSocketState.Open)
                SendSnapshotRequest(product);
            else if(!SnapshotQueue.Contains(product))
                SnapshotQueue.Add(product);
        }

        private void SendSnapshotRequest(string product)
        {
            if (ws == null || ws.State != WebSocketState.Open)
                Logging.Log("Cannot send snapshot request - websocket closed");
            else
            {
                string request = "{\"op\":\"getSymbol\", \"args\": [\"" + product + "\"]}";
                Logging.Log("Send snapshot request: {0}", request);
                ws.Send(request);
            }
        }


        public void HandleData(string data)
        {
            try
            {
                //Logging.Log("Incoming {0}", data);

                //subscriptions
                if (data.Contains("Welcome"))
                {
                    //subscribe streaming depth changes (top 10 levels)
                    string subscription = "{\"op\": \"subscribe\", \"args\": [\"trade\", \"orderBook10\"]}";
                    Logging.Log("Send subscription request: {0}", subscription);
                    ws.Send(subscription);

                    //send request for any products
                    foreach (string product in SnapshotQueue)
                        SendSnapshotRequest(product);
                    SnapshotQueue.Clear();
                }
                else if (data.Contains("data"))
                {
                    //try to deserialise data
                    BitMexStream streamType = JSON.ToObject<BitMexStream>(data);
                    if (streamType.table.Equals("orderBook10"))
                    {
                        ProcessDepthSnapshot(data);
                    }
                    else if (streamType.table.Equals("orderBook25"))
                    {
                        //returned by getSymbol snapshot call
                        ProcessDepth(data);
                    }
                    else if (streamType.table.Equals("trade"))
                    {
                        ProcessTrade(data);
                    }
                }
                else if (data.Contains("error"))
                {
                    Logging.Log("BitMexHandler data error {0}", data);
                    Reconnect();
                }
            }
            catch (Exception ex)
            {
                Logging.Log("BitMexHandler couldnt parse {0} {1}", data, ex.Message);
            }
        }

        private void ProcessTrade(string data)
        {
            try
            {
                BitMexStreamTrade trade = JSON.ToObject<BitMexStreamTrade>(data);

                //notify all new trades
                foreach (BitMexTrade tradeInfo in trade.data)
                {
                    Logging.Log("Incoming trade: {0} {1} {2}", tradeInfo.symbol, tradeInfo.price, tradeInfo.timestamp);
                    if (OnTradeUpdate != null)
                        OnTradeUpdate(null, tradeInfo);
                }
            }
            catch (Exception ex)
            {
                Logging.Log("BitMexHandler Trade {0}", ex.Message);
            }
        }

        private void ProcessDepthSnapshot(string data)
        {
            try
            {
                BitMexStreamDepthSnap depth = JSON.ToObject<BitMexStreamDepthSnap>(data);

                if (depth.data.Count != 1)
                {
                    Logging.Log("BitMexHandler missing depth snap data");
                    return;
                }
                BitMexDepthCache info = depth.data[0];
                string symbol = info.symbol;

                //parse into snapshot
                MarketDataSnapshot snap = new MarketDataSnapshot(symbol);

                foreach (object[] bid in info.bids)
                {
                    decimal price = Convert.ToDecimal(bid[0]);
                    int rawQty = Convert.ToInt32(bid[1]);
                    snap.BidDepth.Add(new MarketDepth(price, rawQty));
                }

                foreach (object[] ask in info.asks)
                {
                    decimal price = Convert.ToDecimal(ask[0]);
                    int rawQty = Convert.ToInt32(ask[1]);
                    snap.AskDepth.Add(new MarketDepth(price, rawQty));
                }

                //notify listeners
                if(OnDepthUpdate != null)
                    OnDepthUpdate(null, snap);

            }
            catch (Exception ex)
            {
                Logging.Log("BitMexHandler Depth {0}", ex.Message);
            }
        }

        private void ProcessDepth(string data)
        {
            try
            {
                BitMexStreamDepth depth = JSON.ToObject<BitMexStreamDepth>(data);

                if (depth.data.Count <= 0)
                    return;

                string symbol = depth.data[0].symbol;

                //create cache container if not already available
                MarketDataSnapshot snap = new MarketDataSnapshot(symbol);

                //normally returns "partial" on response to getSymbol
                if( depth.action.Equals("partial"))
                {
                    foreach (BitMexDepth info in depth.data)
                    {
                        int level = info.level;

                        //extend depth if new levels added
                        if ((info.bidPrice.HasValue || info.bidSize.HasValue) && snap.BidDepth.Count < level + 1)
                            for (int i = snap.BidDepth.Count; i < level + 1; i++)
                                snap.BidDepth.Add(new MarketDepth());

                        if ((info.askPrice.HasValue || info.askSize.HasValue) && snap.AskDepth.Count < level + 1)
                            for (int i = snap.AskDepth.Count; i < level + 1; i++)
                                snap.AskDepth.Add(new MarketDepth());

                        //update values, or blank out if values are null
                        if (info.bidPrice.HasValue || info.bidSize.HasValue)
                        {
                            if (info.bidPrice.HasValue)
                                snap.BidDepth[level].Price = info.bidPrice.Value;

                            if (info.bidSize.HasValue)
                                snap.BidDepth[level].Qty = info.bidSize.Value;
                        }

                        if (info.askPrice.HasValue || info.askSize.HasValue)
                        {
                            if (info.askPrice.HasValue)
                                snap.AskDepth[level].Price = info.askPrice.Value;

                            if (info.askSize.HasValue)
                                snap.AskDepth[level].Qty = info.askSize.Value;
                        }
                    }

                    //notify listeners
                    if (OnDepthUpdate != null)
                        OnDepthUpdate(null, snap);
                }

            }
            catch (Exception ex)
            {
                Logging.Log("BitMexHandler Depth", ex);
            }

        }

        //TODO to avoid spreadsheet locking, download asynchronously and callback when download completed
        public List<BitMexInstrument> DownloadInstrumentList(string state)
        {
            List<BitMexInstrument> instruments = new List<BitMexInstrument>();
            try
            {
                WebClient client = new WebClient();
                string json = client.DownloadString("https://www.bitmex.com/api/v1/instrument");

                if (json == null)
                    Logging.Log("BitMexHandler - instrument download failed");
                else
                {
                    instruments = JSON.ToObject<List<BitMexInstrument>>(json);
                    instruments = instruments.Where(s => state == null || state.Equals(s.state)).OrderBy(s => s.state).ThenBy(s => s.rootSymbol).ThenByDescending(s => s.expiry).ToList();
                }

            }
            catch (Exception ex)
            {
                Logging.Log("BitMexHandler instrument download exception {0}", ex.Message);
            }

            return instruments;
        }

        //TODO to avoid spreadsheet locking, download asynchronously and callback when download completed
        public List<BitMexIndex> DownloadIndex(string symbol, int count, DateTime start, DateTime end)
        {
            List<BitMexIndex> index = new List<BitMexIndex>();
            try
            {
                WebClient client = new WebClient();
                client.QueryString.Add("symbol", symbol);
                if (count >= 0)
                    client.QueryString.Add("count", count.ToString());
                if (start != default(DateTime))
                    client.QueryString.Add("startTime", start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                if (end != default(DateTime) && end >= start && end.Year > 2000)    //excel can pass 1899 as a default date, so validate > y2k
                    client.QueryString.Add("endTime", end.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                 
                string json = client.DownloadString("https://www.bitmex.com/api/v1/trade");

                if (json == null)
                    Logging.Log("BitMexHandler - instrument download failed");
                else
                {
                    index = JSON.ToObject<List<BitMexIndex>>(json);
                    index = index.OrderByDescending(s => s.timestamp).ToList();
                }

            }
            catch (Exception ex)
            {
                Logging.Log("BitMexHandler index download exception {0}", ex.Message);
            }

            return index;
        }
    }

    [Serializable]
    public class BitMexStream
    {
        public string table;
        public string action;
        public List<string> keys;
    }

    [Serializable]
    public class BitMexStreamDepthSnap
    {
        public string table;
        public string action;
        public List<string> keys;
        public List<BitMexDepthCache> data;
    }

    [Serializable]
    public class BitMexDepthCache
    {
        public string symbol;
        public long timestamp;

        public List<object[]> bids = new List<object[]>();
        public List<object[]> asks = new List<object[]>();
    }

    [Serializable]
    public class BitMexStreamDepth
    {
        public string table;
        public string action;
        //public List<string> keys;
        public List<BitMexDepth> data;
    }

    [Serializable]
    public class BitMexDepth
    {
        public string symbol;
        public int level;
        public int? bidSize;
        public decimal? bidPrice;
        public int? askSize;
        public decimal? askPrice;
        public long timestamp;

        public override string ToString()
        {
            return symbol + " " + level + " " + bidSize + " @ " + bidPrice + " / " + askSize + " @ " + askPrice;
        }
    }

    [Serializable]
    public class BitMexInstrument
    {
        public string symbol;
        public string rootSymbol;
        public string state;
        public string typ;
        public string listing;
        public string expiry;
        public string underlying;
        public string buyLeg;
        public string sellLeg;
        public string quoteCurrency;
        public string reference;
        public string referenceSymbol;
        public decimal tickSize;
        public long multiplier;
        public string settlCurrency;
        public decimal initMargin;
        public decimal maintMargin;
        public decimal limit;
        public string openingTimestamp;
        public string closingTimestamp;
        public decimal prevClosePrice;
        public decimal limitDownPrice;
        public decimal limitUpPrice;
        public decimal volume;
        public bool isQuanto;
        public bool isInverse;
        public decimal totalVolume;
        public decimal vwap;
        public decimal openInterest;
        public string underlyingSymbol;
        public decimal underlyingToSettleMultiplier;
        public decimal highPrice;
        public decimal lowPrice;
        public decimal lastPrice;
        //...
    }

    [Serializable]
    public class BitMexStreamTrade
    {
        public string table;
        public string action;
        public List<string> keys;
        public List<BitMexTrade> data;
    }

    [Serializable]
    public class BitMexTrade
    {
        public long timestamp;
        public string symbol;
        public decimal size;
        public decimal price;
    }

    [Serializable]
    public class BitMexIndex
    {
        public string timestamp;
        public string symbol;
        public string side;
        public decimal size;
        public decimal price;
        public string tickDirection;
    }

}
