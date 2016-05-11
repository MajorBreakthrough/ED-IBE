﻿using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Threading;
using ZeroMQ;
using IBE.Enums_and_Utility_Classes;
using System.Globalization;
using System.Net;
using CodeProject.Dialog;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using IBE.Enums_and_Utility_Classes;
using Newtonsoft.Json.Linq;
using System.Text;

namespace IBE.EDDN
{

    public class EDDNCommunicator : IDisposable
    {

        private enum enSchema
        {
            Real = 0,
            Test = 1
        }

        public enum enInterface
        {
            API = 0,
            OCR = 1
        }

#region dispose region

        // Flag: Has Dispose already been called?
        private bool m_SenderIsActivated;
bool disposed = false;
        // Instantiate a SafeHandle instance.
        SafeHandle handle = new SafeFileHandle(IntPtr.Zero, true);

        // Public implementation of Dispose pattern callable by consumers.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
            {
                handle.Dispose();

                // Free any other managed objects here.
                StopEDDNListening();

                if (m_DuplicateFilter != null)
                    m_DuplicateFilter.Dispose(); 

                m_DuplicateFilter = null;

                if (m_DuplicateRelayFilter != null)
                    m_DuplicateRelayFilter.Dispose(); 

                m_DuplicateRelayFilter = null;
            }

            // Free any unmanaged objects here.
            disposed = true;
        }

#endregion

#region event handler

        [System.ComponentModel.Browsable(true)]
        public event EventHandler<DataChangedEventArgs> DataChangedEvent;

        protected virtual void OnLocationChanged(DataChangedEventArgs e)
        {
            EventHandler<DataChangedEventArgs> myEvent = DataChangedEvent;
            if (myEvent != null)
            {
                myEvent(this, e);
            }
        }

        public class DataChangedEventArgs : EventArgs
        {
            public DataChangedEventArgs(enDataTypes dType)
            {
                DataType = dType;    
            }

            public enDataTypes DataType               { get; set; }
        }

        [Flags] public enum enDataTypes
        {
            NoChanges           =  0,
            RecieveData         =  1,
            ImplausibleData     =  2,
            Statistics          =  4,
            DataImported        =  8    
        }

        public enum enMessageTypes
        {
            unknown             =  0,
            Commodity_V1        =  1,
            Commodity_V2        =  2,
            Shipyard_V1         =  11,
            Outfitting_V1       =  21    
        }


 #endregion

        private Thread                              _Spool2EDDN_Commodity;   
        private Thread                              _Spool2EDDN_Outfitting;   
        private Thread                              _Spool2EDDN_Shipyard;   
        private Queue                               _Send_MarketData_API = new Queue(100,10);
        private Queue                               _Send_MarketData_OCR = new Queue(100,10);
        private Queue                               _Send_Outfitting     = new Queue(100,10);
        private Queue                               _Send_Shipyard       = new Queue(100,10);
        private SingleThreadLogger                  _logger;
        private System.Timers.Timer                 _SendDelayTimer_Commodity;
        private System.Timers.Timer                 _SendDelayTimer_Outfitting;
        private System.Timers.Timer                 _SendDelayTimer_Shipyard;
        private StreamWriter                        m_EDDNSpooler = null;
        private Dictionary<String, EDDNStatistics>  m_StatisticDataSW = new Dictionary<String, EDDNStatistics>();
        private Dictionary<String, EDDNStatistics>  m_StatisticDataRL = new Dictionary<String, EDDNStatistics>();
        private Dictionary<String, EDDNStatistics>  m_StatisticDataCM = new Dictionary<String, EDDNStatistics>();
        private Dictionary<String, EDDNStatistics>  m_StatisticDataMT = new Dictionary<String, EDDNStatistics>();
        private List<String>                        m_RejectedData;
        private List<String>                        m_RawData;
        private EDDNDuplicateFilter                 m_DuplicateFilter       = new EDDNDuplicateFilter(new TimeSpan(0,5,0));
        private EDDNDuplicateFilter                 m_DuplicateRelayFilter  = new EDDNDuplicateFilter(new TimeSpan(0,0,30));
        private Dictionary<String, EDDNReciever>    m_Reciever;
        private List<String>                        m_Relays    = new List<string>() { "tcp://eddn-relay.elite-markets.net:9500", 
                                                                                       "tcp://eddn-relay.ed-td.space:9500"};

        private Tuple<String, DateTime>             m_ID_of_Commodity_Station = new Tuple<String, DateTime>("", new DateTime());
        private Tuple<String, DateTime>             m_ID_of_Outfitting_Station = new Tuple<String, DateTime>("", new DateTime());
        private Tuple<String, DateTime>             m_ID_of_Shipyard_Station = new Tuple<String, DateTime>("", new DateTime());


        public EDDNCommunicator()
        {

            m_Reciever = new Dictionary<String, EDDNReciever>();

            _SendDelayTimer_Commodity             = new System.Timers.Timer(2000);
            _SendDelayTimer_Commodity.AutoReset   = false;
            _SendDelayTimer_Commodity.Elapsed     += new System.Timers.ElapsedEventHandler(this.SendDelayTimerCommodity_Elapsed);

            _SendDelayTimer_Outfitting            = new System.Timers.Timer(2000);
            _SendDelayTimer_Outfitting.AutoReset  = false;
            _SendDelayTimer_Outfitting.Elapsed    += new System.Timers.ElapsedEventHandler(this.SendDelayTimerOutfitting_Elapsed);

            _SendDelayTimer_Shipyard              = new System.Timers.Timer(2000);
            _SendDelayTimer_Shipyard.AutoReset    = false;
            _SendDelayTimer_Shipyard.Elapsed      += new System.Timers.ElapsedEventHandler(this.SendDelayTimerShipyard_Elapsed);

            _logger                     = new SingleThreadLogger(ThreadLoggerType.EddnSubscriber);

            m_RejectedData              = new List<String>();
            m_RawData                   = new List<String>();

            UserIdentification();
        }

#region receive

        /// <summary>
        /// starts EDDN-listening (if already started the EDDN-lister will be stopped and restarted)
        /// </summary>
        public void StartEDDNListening()
        {
            try
            {
                StopEDDNListening();

                foreach (String adress in m_Relays)
                {
                    var newReciever = new EDDNReciever(adress);
                    newReciever.StartListen();
                    newReciever.DataRecieved += RecievedEDDNData;

                    m_Reciever.Add(adress, newReciever);
                }

            }
            catch (Exception ex)
            {
                throw new Exception("Error while starting the EDDN-Listener", ex);
            }
        }

        /// <summary>
        /// stops EDDN-listening if started
        /// </summary>
        public void StopEDDNListening()
        {
            try
            {
                foreach (var recieverKVP in m_Reciever)
                    recieverKVP.Value.Dispose();

                m_Reciever.Clear();
            }
            catch (Exception ex)
            {
                throw new Exception("Error while stopping the EDDN-Listener", ex);
            }
        }

        /// <summary>
        /// processing of the recieved data
        /// </summary>                     
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RecievedEDDNData(object sender, EDDN.EDDNRecievedArgs e)
        {
            String[] DataRows = new String[0];
            String nameAndVersion = String.Empty;
            String name = String.Empty;
            String uploaderID = String.Empty;
            Boolean SimpleEDDNCheck = false;
            List<String> importData = new List<String>();
            enSchema ownSchema;
            enSchema dataSchema;

            try
            {

                if (Program.DBCon.getIniValue<Boolean>("EDDN", "SpoolEDDNToFile", false.ToString(), false))
                {
                    if (m_EDDNSpooler == null)
                    {
                        if (!File.Exists(Program.GetDataPath(@"Logs\EddnOutput.txt")))
                            m_EDDNSpooler = File.CreateText(Program.GetDataPath(@"Logs\EddnOutput.txt"));
                        else
                            m_EDDNSpooler = File.AppendText(Program.GetDataPath(@"Logs\EddnOutput.txt"));
                    }
                    m_EDDNSpooler.WriteLine(e.RawData);
                }

                ownSchema = Program.DBCon.getIniValue<enSchema>(IBE.EDDN.EDDNView.DB_GROUPNAME, "Schema", "Real", false);

                switch (e.InfoType)
                {

                    case EDDN.EDDNRecievedArgs.enMessageInfo.Commodity_v2_Recieved:

                        EDDN.EDDNCommodity_v2 DataObject = (EDDN.EDDNCommodity_v2)e.Data;

                        if(m_DuplicateRelayFilter.DataAccepted(DataObject.Header.UploaderID, DataObject.Message.SystemName + "|" + DataObject.Message.StationName, DataObject.Message.Commodities.Count().ToString(), DateTime.Parse(DataObject.Message.Timestamp)))
                        { 
                            UpdateStatisticDataMsg(enMessageTypes.Commodity_V2);
                            UpdateRawData(String.Format("{0}\r\n(from {2})\r\n{1}", e.Message, e.RawData, e.Adress));

                            // process only if it's the correct schema
                            dataSchema = ((EDDN.EDDNCommodity_v2)e.Data).isTest() ? enSchema.Test : enSchema.Real;

                            if (ownSchema == dataSchema)
                            {
                                Debug.Print("handle v2 message");



                                // Don't import our own uploads...
                                if (DataObject.Header.UploaderID != UserIdentification())
                                {
                                    DataRows = DataObject.getEDDNCSVImportStrings();
                                    nameAndVersion = String.Format("{0} / {1}", DataObject.Header.SoftwareName, DataObject.Header.SoftwareVersion);
                                    name = String.Format("{0}", DataObject.Header.SoftwareName);
                                    uploaderID = DataObject.Header.UploaderID;
                                }
                                else
                                    Debug.Print("handle v2 rejected (it's our own message)");
                            }
                            else
                                Debug.Print("handle v2 rejected (wrong schema)");
                        }
                        else
                            Debug.Print("handle v2 rejected (double recieved)");

                        break;

                    case EDDN.EDDNRecievedArgs.enMessageInfo.Outfitting_v1_Recieved:

                        UpdateStatisticDataMsg(enMessageTypes.Outfitting_V1);
                        //UpdateRawData("recieved shipyard message ignored (coming feature)");
                        Debug.Print("recieved shipyard message ignored");
                        UpdateRawData(String.Format("{0}\r\n(from {2})\r\n{1}", e.Message, e.RawData, e.Adress));

                        break;

                    case EDDN.EDDNRecievedArgs.enMessageInfo.Shipyard_v1_Recieved:
                        UpdateStatisticDataMsg(enMessageTypes.Shipyard_V1);
                        //UpdateRawData("recieved shipyard message ignored (coming feature)");
                        Debug.Print("recieved shipyard message ignored");
                        break;

                    case EDDN.EDDNRecievedArgs.enMessageInfo.UnknownData:
                        UpdateStatisticDataMsg(enMessageTypes.unknown);
                        UpdateRawData(String.Format("{0}\r\n(from {2})\r\n{1}", e.Message, e.RawData, e.Adress));
                        UpdateRawData("Recieved a unknown EDDN message:" + Environment.NewLine + e.Message + Environment.NewLine + e.RawData);
                        Debug.Print("handle unkown message");
                        break;

                    case EDDN.EDDNRecievedArgs.enMessageInfo.ParseError:
                        UpdateRawData(String.Format("{0}\r\n(from {2})\r\n{1}", e.Message, e.RawData, e.Adress));
                        Debug.Print("handle error message");
                        UpdateRawData("Error while processing recieved EDDN data:" + Environment.NewLine + e.Message + Environment.NewLine + e.RawData);

                        break;

                }

                if (DataRows != null && DataRows.GetUpperBound(0) >= 0)
                {
                    UpdateStatisticData(DataRows.GetUpperBound(0) + 1, nameAndVersion, e.Adress, uploaderID);

                    foreach (String DataRow in DataRows)
                    {
                        if(m_DuplicateFilter.DataAccepted(DataRow))
                        {
                            bool isTrusty = (Program.Data.BaseData.tbtrustedsenders.Rows.Find(name) != null);

                            // data is plausible ?
                            if (isTrusty || (!Program.PlausibiltyCheck.CheckPricePlausibility(new string[] { DataRow }, SimpleEDDNCheck)))
                            {

                                // import is wanted ?
                                if (Program.DBCon.getIniValue<Boolean>("EDDN", "ImportEDDN", false.ToString(), false))
                                {
                                    // collect importable data
                                    Debug.Print("import :" + DataRow);
                                    importData.Add(DataRow);
                                }

                            }
                            else
                            {
                                Debug.Print("implausible :" + DataRow);
                                // data is implausible
                                string InfoString = string.Format("IMPLAUSIBLE DATA : \"{2}\" from {0}/ID=[{1}]", nameAndVersion, uploaderID, DataRow);

                                UpdateRejectedData(InfoString);

                                if (Program.DBCon.getIniValue<Boolean>("EDDN", "SpoolImplausibleToFile", false.ToString(), false))
                                {

                                    FileStream LogFileStream = null;
                                    string FileName = Program.GetDataPath(@"Logs\EddnImplausibleOutput.txt");

                                    if (File.Exists(FileName))
                                        LogFileStream = File.Open(FileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                                    else
                                        LogFileStream = File.Create(FileName);

                                    LogFileStream.Write(System.Text.Encoding.Default.GetBytes(InfoString + "\n"), 0, System.Text.Encoding.Default.GetByteCount(InfoString + "\n"));
                                    LogFileStream.Close();
                                }
                            }
                        }
                    }

                    // have we collected importable data -> then import now
                    if (importData.Count() > 0)
                    {
                        Program.Data.ImportPricesFromCSVStrings(importData.ToArray(), SQL.EliteDBIO.enImportBehaviour.OnlyNewer, SQL.EliteDBIO.enDataSource.fromEDDN);
                        DataChangedEvent.Raise(this, new DataChangedEventArgs(enDataTypes.DataImported));
                    }

                }
            }
            catch (Exception ex)
            {
                UpdateRawData("Error while processing recieved EDDN data:" + Environment.NewLine + ex.GetBaseException().Message + Environment.NewLine + ex.StackTrace);
            }
        }

        /// <summary>
        /// returns the number of active listeners
        /// </summary>
        public Int32 ListenersRunning
        {
            get
            {
                Int32 count = 0;

                foreach (var recieverKVP in m_Reciever)
                {
                    if(recieverKVP.Value.IsListening)
                        count++;
                }

                return count;
            }
        }

        /// <summary>
        /// updates the content of the "recieve" data
        /// </summary>
        /// <param name="info"></param>
        private void UpdateRawData(String info)
        {
            Int32 maxSize = 10;

            try
            {
                m_RawData.Add(info);

                if (m_RawData.Count() > maxSize)
                    m_RawData.RemoveRange(0, m_RawData.Count()-maxSize);

                DataChangedEvent.Raise(this, new DataChangedEventArgs(enDataTypes.RecieveData));
            }
            catch (Exception ex)
            {
                throw new Exception("Error while updating recieve data", ex);
            }
        }

        /// <summary>
        /// updates the content of the "implausible" data
        /// </summary>
        /// <param name="info"></param>
        private void UpdateRejectedData(String info)
        {
            Int32 maxSize = 100;

            try
            {
                m_RejectedData.Add(info);

                if (m_RejectedData.Count() > maxSize)
                    m_RejectedData.RemoveRange(0, m_RejectedData.Count()-maxSize);

                DataChangedEvent.Raise(this, new DataChangedEventArgs(enDataTypes.ImplausibleData));
            }
            catch (Exception ex)
            {
                throw new Exception("Error while updating implausible data", ex);
            }
        }

        /// <summary>
        /// refreshes the info about recieved messagetypes
        /// </summary>
        /// <param name="dataCount"></param>
        /// <param name="SoftwareID"></param>
        private void UpdateStatisticDataMsg(enMessageTypes mType)
        {
            try
            {
                if (!m_StatisticDataMT.ContainsKey(mType.ToString()))
                    m_StatisticDataMT.Add(mType.ToString(), new EDDNStatistics());

                m_StatisticDataMT[mType.ToString()].MessagesReceived += 1;

            }
            catch (Exception ex)
            {
                throw new Exception("Error while updating statistics (mt)", ex);
            }
        }

        /// <summary>
        /// refreshes the info about software versions and recieved messages
        /// </summary>
        /// <param name="dataCount"></param>
        /// <param name="SoftwareID"></param>
        private void UpdateStatisticData(int dataCount,string SoftwareID, String relay, String commander)
        {
            try
            {
                if (!m_StatisticDataSW.ContainsKey(SoftwareID))
                    m_StatisticDataSW.Add(SoftwareID, new EDDNStatistics());

                m_StatisticDataSW[SoftwareID].MessagesReceived += 1;
                m_StatisticDataSW[SoftwareID].DatasetsReceived += dataCount;

                if (!m_StatisticDataRL.ContainsKey(relay))
                    m_StatisticDataRL.Add(relay, new EDDNStatistics());

                m_StatisticDataRL[relay].MessagesReceived += 1;
                m_StatisticDataRL[relay].DatasetsReceived += dataCount;

                if (!m_StatisticDataCM.ContainsKey(commander))
                    m_StatisticDataCM.Add(commander, new EDDNStatistics());

                m_StatisticDataCM[commander].MessagesReceived += 1;
                m_StatisticDataCM[commander].DatasetsReceived += dataCount;

                DataChangedEvent.Raise(this, new DataChangedEventArgs(enDataTypes.Statistics));

            }
            catch (Exception ex)
            {
                throw new Exception("Error while updating statistics", ex);
            }
        }

        public List<String> RawData
        {
            get
            {
                return m_RawData;
            }
        }
        public Dictionary<String, EDDNStatistics> StatisticDataSW
        {
            get
            {
                return m_StatisticDataSW;
            }
        }
        public Dictionary<String, EDDNStatistics> StatisticDataRL
        {
            get
            {
                return m_StatisticDataRL;
            }
        }
        public Dictionary<String, EDDNStatistics> StatisticDataCM
        {
            get
            {
                return m_StatisticDataCM;
            }
        }
        public Dictionary<String, EDDNStatistics> StatisticDataMT
        {
            get
            {
                return m_StatisticDataMT;
            }
        }
        public List<String> RejectedData
        {
            get
            {
                return m_RejectedData;
            }
        }

#endregion

#region send

        /// <summary>
        /// returns if the sender is active
        /// </summary>
        public bool SenderIsActivated
        {
            get
            {
                return m_SenderIsActivated;
            }
        }

        /// <summary>
        /// activatesender the sender
        /// </summary>
        public void ActivateSender()
        {
            m_SenderIsActivated = true;
        }

        /// <summary>
        /// deactivates the sender
        /// </summary> 
        public void DeactivateSender()
        {
            m_SenderIsActivated = false;

            SendingReset();
        }

        /// <summary>
        /// register everything for sending with this function.
        /// 2 seconds after the last registration all data will be sent automatically
        /// </summary>
        /// <param name="stationData"></param>
        public void SendMarketData(CsvRow CommodityData, enInterface usedInterface)
        {
            if(m_SenderIsActivated && 
               Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "QuickDecisionValue", false.ToString()) &&
                ((Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "EDDNPostCompanionData", true.ToString(), false) && (usedInterface == enInterface.API)) || 
                 (Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "EDDNPostOCRData",       true.ToString(), false) && (usedInterface == enInterface.OCR))))
            {

                // register next data row
                if(usedInterface == enInterface.API)
                {
                    if((m_ID_of_Commodity_Station.Item1 != (CommodityData.SystemName + "|" + CommodityData.StationName)) || ((DateTime.Now - m_ID_of_Commodity_Station.Item2).TotalMinutes >= 60))
                    { 
                        _Send_MarketData_API.Enqueue(CommodityData);

                        // here we get only the id, the time will be set after sending
                        m_ID_of_Commodity_Station = new Tuple<String, DateTime>(CommodityData.SystemName + "|" + CommodityData.StationName, DateTime.Now - new TimeSpan(0,65,0));

                        // reset the timer
                        _SendDelayTimer_Commodity.Start();

                    }
                }
                else
                { 
                    _Send_MarketData_OCR.Enqueue(CommodityData);

                    // reset the timer
                    _SendDelayTimer_Commodity.Start();
                }


            }
        }

        /// <summary>
        /// register everything for sending with this function.
        /// 2 seconds after the last registration all data will be sent automatically
        /// </summary>
        /// <param name="stationData"></param>
        public void SendMarketData(List<CsvRow> csvRowList, enInterface usedInterface)
        {
            if(m_SenderIsActivated && 
               Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "QuickDecisionValue", false.ToString()) &&
                ((Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "EDDNPostCompanionData", true.ToString(), false) && (usedInterface == enInterface.API)) || 
                 (Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "EDDNPostOCRData",       true.ToString(), false) && (usedInterface == enInterface.OCR))))
            {
                if((m_ID_of_Commodity_Station.Item1 != (csvRowList[0].SystemName + "|" + csvRowList[0].StationName)) || ((DateTime.Now - m_ID_of_Commodity_Station.Item2).TotalMinutes >= 60) || (usedInterface == enInterface.OCR))
                { 
                    // reset the timer
                    _SendDelayTimer_Commodity.Start();

                    // register rows
                    foreach (CsvRow csvRowListItem in csvRowList)
                        if(usedInterface == enInterface.API)
                            _Send_MarketData_API.Enqueue(csvRowListItem);
                        else
                            _Send_MarketData_OCR.Enqueue(csvRowListItem);

                    // reset the timer
                    _SendDelayTimer_Commodity.Start();

                    if(usedInterface == enInterface.API)
                    { 
                        // here we get only the id, the time will be set after sending
                        m_ID_of_Commodity_Station = new Tuple<String, DateTime>(csvRowList[0].SystemName + "|" + csvRowList[0].StationName, DateTime.Now - new TimeSpan(0,65,0));
                    }

                }
            }
        }

        /// <summary>
        /// register everything for sending with this function.
        /// 2 seconds after the last registration all data will be sent automatically
        /// </summary>
        /// <param name="stationData"></param>
        public void SendMarketData(String[] csv_Strings, enInterface usedInterface)
        {
            if(m_SenderIsActivated && 
               Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "QuickDecisionValue", false.ToString()) &&
                ((Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "EDDNPostCompanionData", true.ToString(), false) && (usedInterface == enInterface.API)) || 
                 (Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "EDDNPostOCRData",       true.ToString(), false) && (usedInterface == enInterface.OCR))))
            {
                CsvRow testRow = new CsvRow(csv_Strings[0]);

                if((m_ID_of_Commodity_Station.Item1 != (testRow.SystemName + "|" + testRow.StationName)) || ((DateTime.Now - m_ID_of_Commodity_Station.Item2).TotalMinutes >= 60) || (usedInterface == enInterface.OCR))
                { 
                    // reset the timer
                    _SendDelayTimer_Commodity.Start();

                    // register rows
                    foreach (String csvRowString in csv_Strings)
                        if(usedInterface == enInterface.API)
                            _Send_MarketData_API.Enqueue(new CsvRow(csvRowString));
                        else
                            _Send_MarketData_OCR.Enqueue(new CsvRow(csvRowString));

                    // reset the timer
                    _SendDelayTimer_Commodity.Start();

                    if(usedInterface == enInterface.API)
                    { 
                        // here we get only the id, the time will be set after sending
                        m_ID_of_Commodity_Station = new Tuple<String, DateTime>(testRow.SystemName + "|" + testRow.StationName, DateTime.Now - new TimeSpan(0,65,0));
                    }

                }
            }
        }

        /// <summary>
        /// send the outfitting data of this station
        /// </summary>
        /// <param name="stationData">json object with companion data</param>
        public void SendOutfittingData(JObject dataObject)
        {
            Int32 objectCount = 0;
            Boolean writeToFile = false;
            StreamWriter writer = null;
            String debugFile = @"C:\temp\outfitting_ibe.csv";

            try
            {
                if(m_SenderIsActivated && Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "EDDNPostOutfittingData", true.ToString(), false))
                {
                    IBECompanion.CompanionConverter cmpConverter = new IBECompanion.CompanionConverter();
                    String systemName   = dataObject.SelectToken("lastSystem.name").ToString();
                    String stationName = dataObject.SelectToken("lastStarport.name").ToString();

                    if((m_ID_of_Outfitting_Station.Item1 != systemName + "|" + stationName) || ((DateTime.Now - m_ID_of_Outfitting_Station.Item2).TotalMinutes >= 60))
                    { 
                        m_ID_of_Outfitting_Station = new Tuple<String, DateTime>(systemName +"|" + stationName, DateTime.Now);

                        StringBuilder outfittingStringEDDN = new StringBuilder();

                        outfittingStringEDDN.Append(String.Format("\"message\": {{"));

                        outfittingStringEDDN.Append(String.Format("\"systemName\":\"{0}\", ",dataObject.SelectToken("lastSystem.name").ToString()));
                        outfittingStringEDDN.Append(String.Format("\"stationName\":\"{0}\", ",dataObject.SelectToken("lastStarport.name").ToString()));

                        outfittingStringEDDN.Append(String.Format("\"timestamp\":\"{0}\", ", DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + DateTime.Now.ToString("zzz", CultureInfo.InvariantCulture)));

                        outfittingStringEDDN.Append(String.Format("\"modules\": ["));

                        if(writeToFile)
                        { 
                            if(File.Exists(debugFile))
                                File.Delete(debugFile);

                            writer = new StreamWriter(File.OpenWrite(debugFile));
                        }


                        foreach (JToken outfittingItem in dataObject.SelectTokens("lastStarport.modules.*"))
                        {
                            OutfittingObject outfitting = cmpConverter.GetOutfittingFromCompanion(outfittingItem, false);

                            if(outfitting != null)
                            { 
                                if(objectCount > 0)
                                    outfittingStringEDDN.Append(", {");
                                else
                                    outfittingStringEDDN.Append("{");

                                if(writeToFile)
                                {
                                    writer.WriteLine(String.Format("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}", 
                                        systemName, stationName, outfitting.Category, outfitting.Name, outfitting.Mount, 
                                        outfitting.Guidance, outfitting.Ship, outfitting.Class, outfitting.Rating, 
                                        DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + DateTime.Now.ToString("zzz", CultureInfo.InvariantCulture)));
                                }

                                outfittingStringEDDN.Append(String.Format("\"category\":\"{0}\", ", outfitting.Category));
                                outfittingStringEDDN.Append(String.Format("\"name\":\"{0}\", ", outfitting.Name));
                                outfittingStringEDDN.Append(String.Format("\"class\":\"{0}\", ", outfitting.Class));
                                outfittingStringEDDN.Append(String.Format("\"rating\":\"{0}\", ", outfitting.Rating));

                                switch (outfitting.Category)
                                {
                                    case EDDN.OutfittingObject.Cat_Hardpoint:

                                        outfittingStringEDDN.Append(String.Format("\"mount\":\"{0}\", ", outfitting.Mount));
                                        if(outfitting.Guidance != null)
                                            outfittingStringEDDN.Append(String.Format("\"guidance\":\"{0}\", ", outfitting.Guidance));
                                        break;

                                    case EDDN.OutfittingObject.Cat_Standard:

                                        if(outfitting.Ship != null)
                                            outfittingStringEDDN.Append(String.Format("\"ship\":\"{0}\", ", outfitting.Ship));
                                        break;
                                }

                                outfittingStringEDDN.Remove(outfittingStringEDDN.Length-1, 1);
                                outfittingStringEDDN.Replace(",", "}", outfittingStringEDDN.Length-1, 1);

                                objectCount++;
                            }
                        } 

                        outfittingStringEDDN.Append("]}");

                        if(objectCount > 0)
                        { 
                            _Send_Outfitting.Enqueue(outfittingStringEDDN);
                            _SendDelayTimer_Outfitting.Start();
                            m_ID_of_Outfitting_Station = new Tuple<String, DateTime>(systemName +"|" + stationName, DateTime.Now);
                        }

                        if(writeToFile)
                        {
                            writer.Close();
                            writer.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error while extracting outfitting data for eddn", ex);
            }
        }

        /// <summary>
        /// send the shipyard data of this station
        /// </summary>
        /// <param name="stationData">json object with companion data</param>
        public void SendShipyardData(JObject dataObject)
        {
            Int32 objectCount = 0;
            Boolean writeToFile = false;
            StreamWriter writer = null;
            String debugFile = @"C:\temp\shipyard_ibe.csv";

            try
            {
                if(m_SenderIsActivated && Program.DBCon.getIniValue<Boolean>(IBE.EDDN.EDDNView.DB_GROUPNAME, "EDDNPostShipyardData", true.ToString(), false))
                {
                    IBECompanion.CompanionConverter cmpConverter = new IBECompanion.CompanionConverter();  

                    String systemName   = dataObject.SelectToken("lastSystem.name").ToString();
                    String stationName = dataObject.SelectToken("lastStarport.name").ToString();

                    if((m_ID_of_Shipyard_Station.Item1 != systemName + "|" + stationName) || ((DateTime.Now - m_ID_of_Shipyard_Station.Item2).TotalMinutes >= 60))
                    { 
                        StringBuilder shipyardStringEDDN = new StringBuilder();

                        shipyardStringEDDN.Append(String.Format("\"message\": {{"));

                        shipyardStringEDDN.Append(String.Format("\"systemName\":\"{0}\", ",dataObject.SelectToken("lastSystem.name").ToString()));
                        shipyardStringEDDN.Append(String.Format("\"stationName\":\"{0}\", ",dataObject.SelectToken("lastStarport.name").ToString()));

                        shipyardStringEDDN.Append(String.Format("\"timestamp\":\"{0}\", ", DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + DateTime.Now.ToString("zzz", CultureInfo.InvariantCulture)));

                        shipyardStringEDDN.Append(String.Format("\"ships\": ["));

                        if(writeToFile)
                        { 
                            if(File.Exists(debugFile))
                                File.Delete(debugFile);

                            writer = new StreamWriter(File.OpenWrite(debugFile));
                        }

                        if(dataObject.SelectToken("lastStarport.ships", false) != null)
                        { 
                            List<JToken> allShips = dataObject.SelectTokens("lastStarport.ships.shipyard_list.*").ToList();
                            allShips.AddRange(dataObject.SelectTokens("lastStarport.ships.unavailable_list.[*]").ToList());

                            foreach (JToken outfittingItem in allShips)
                            {
                                ShipyardObject shipyardItem = cmpConverter.GetShipFromCompanion(outfittingItem, false);

                                if(shipyardItem != null)
                                { 
                                    if(writeToFile)
                                    {
                                        writer.WriteLine(String.Format("{0},{1},{2},{3}", 
                                            systemName, stationName, shipyardItem.Name, 
                                            DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + DateTime.Now.ToString("zzz", CultureInfo.InvariantCulture)));
                                    }

                                    shipyardStringEDDN.Append(String.Format("\"{0}\", ", shipyardItem.Name));

                                    objectCount++;
                                }
                            } 

                            shipyardStringEDDN.Remove(shipyardStringEDDN.Length-2, 2);
                            shipyardStringEDDN.Append("]}");

                            if(objectCount > 0)
                            { 
                                _Send_Shipyard.Enqueue(shipyardStringEDDN);
                                _SendDelayTimer_Shipyard.Start();
                                m_ID_of_Shipyard_Station = new Tuple<String, DateTime>(systemName +"|" + stationName, DateTime.Now);
                            }

                            if(writeToFile)
                            {
                                writer.Close();
                                writer.Dispose();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error while extracting shipyard data for eddn", ex);
            }
        }


        /// <summary>
        /// internal send routine for registered data:
        /// It's called by the delay-timer "_SendDelayTimer_Commodity"
        /// </summary>
        private void SendMarketData_i()
        {
            try
            {
                Queue activeQueue = null; 
                String UserID;
                EDDNCommodity_v2 Data;
                String TimeStamp;
                String commodity;

                do{

                    TimeStamp   = DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + DateTime.Now.ToString("zzz", CultureInfo.InvariantCulture);
                    UserID      = UserIdentification();
                    Data        = new EDDNCommodity_v2();

                    // test or real ?
                    if (Program.DBCon.getIniValue(IBE.EDDN.EDDNView.DB_GROUPNAME, "Schema", "Real", false) == "Test")
                        Data.SchemaRef = "http://schemas.elite-markets.net/eddn/commodity/2/test";
                    else
                        Data.SchemaRef = "http://schemas.elite-markets.net/eddn/commodity/2";

                    if(_Send_MarketData_API.Count > 0)
                    {
                        // fill the header
                        Data.Header = new EDDNCommodity_v2.Header_Class()
                        {
                            SoftwareName = "ED-IBE (API)",
                            SoftwareVersion = VersionHelper.Parts(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version, 3),
                            GatewayTimestamp = TimeStamp,
                            UploaderID = UserID
                        };

                        activeQueue = _Send_MarketData_API;
                    }
                    else
                    { 
                        // fill the header
                        Data.Header = new EDDNCommodity_v2.Header_Class()
                        {
                            SoftwareName = "ED-IBE (OCR)",
                            SoftwareVersion = VersionHelper.Parts(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version, 3),
                            GatewayTimestamp = TimeStamp,
                            UploaderID = UserID
                        };

                        activeQueue = _Send_MarketData_OCR;
                    }

                    // prepare the message object
                    Data.Message = new EDDNCommodity_v2.Message_Class()
                    {
                        SystemName = "",
                        StationName = "",
                        Timestamp = TimeStamp,
                        Commodities = new EDDNCommodity_v2.Commodity_Class[activeQueue.Count]
                    };

                    // collect the commodity data
                    for (int i = 0; i <= Data.Message.Commodities.GetUpperBound(0); i++)
                    {
                        CsvRow Row = (CsvRow)activeQueue.Dequeue();

                        commodity = Row.CommodityName;

                        // if it's a user added commodity send it anyhow to see that there's a unknown commodity
                        if (commodity.Equals(Program.COMMODITY_NOT_SET))
                            commodity = Row.CommodityName;

                        Data.Message.Commodities[i] = new EDDNCommodity_v2.Commodity_Class()
                        {
                            Name = commodity,
                            BuyPrice = (Int32)Math.Floor(Row.BuyPrice),
                            SellPrice = (Int32)Math.Floor(Row.SellPrice),
                            Demand = (Int32)Math.Floor(Row.Demand),
                            DemandLevel = (Row.DemandLevel == "") ? null : Row.DemandLevel,
                            Supply = (Int32)Math.Floor(Row.Supply),
                            SupplyLevel = (Row.SupplyLevel == "") ? null : Row.SupplyLevel,
                        };

                        if(!String.IsNullOrEmpty(Data.Message.Commodities[i].DemandLevel))
                            Data.Message.Commodities[i].DemandLevel         = char.ToUpper(Data.Message.Commodities[i].DemandLevel[0]) + Data.Message.Commodities[i].DemandLevel.Substring(1);

                        if(!String.IsNullOrEmpty(Data.Message.Commodities[i].SupplyLevel))
                            Data.Message.Commodities[i].SupplyLevel         = char.ToUpper(Data.Message.Commodities[i].SupplyLevel[0]) + Data.Message.Commodities[i].SupplyLevel.Substring(1);

                        if (i == 0)
                        {
                            Data.Message.SystemName = Row.SystemName;
                            Data.Message.StationName = Row.StationName;
                        }

                    }

                    using (var client = new WebClient())
                    {
                        try
                        {
                            Debug.Print(JsonConvert.SerializeObject(Data, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore }));

                            client.UploadString("http://eddn-gateway.elite-markets.net:8080/upload/", "POST", JsonConvert.SerializeObject(Data, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore }));
                        }
                        catch (WebException ex)
                        {
                            _logger.Log("Error uploading json (commodity)", true);
                            _logger.Log(ex.ToString(), true);
                            _logger.Log(ex.Message, true);
                            _logger.Log(ex.StackTrace, true);
                            if (ex.InnerException != null)
                                _logger.Log(ex.InnerException.ToString(), true);

                            using (WebResponse response = ex.Response)
                            {
                                using (Stream data = response.GetResponseStream())
                                {
                                    if (data != null)
                                    {
                                        StreamReader sr = new StreamReader(data);
                                        MsgBox.Show(sr.ReadToEnd(), "Error while uploading to EDDN (v2)");
                                    }
                                }
                            }
                        }
                        finally
                        {
                            client.Dispose();
                        }

                        if(activeQueue == _Send_MarketData_API)
                        { 
                            // data over api sent, set cooldown time
                            m_ID_of_Commodity_Station = new Tuple<String, DateTime>(m_ID_of_Commodity_Station.Item1, DateTime.Now);
                        }

                    }

                    // retry, if the ocr-queue has entries and the last queue was then api-queue
	            } while ((activeQueue != _Send_MarketData_OCR) && (_Send_MarketData_OCR.Count > 0));
            }
            catch (Exception ex)
            {
                _logger.Log("Error uploading json (commodity)", true);
                _logger.Log(ex.ToString(), true);
                _logger.Log(ex.Message, true);
                _logger.Log(ex.StackTrace, true);
                if (ex.InnerException != null)
                    _logger.Log(ex.InnerException.ToString(), true);

                cErr.processError(ex, "Error in EDDN-Sending-Thread (commodity)");
            }

        }

        /// <summary>
        /// internal send routine for registered data:
        /// It's called by the delay-timer "_SendDelayTimer_Commodity"
        /// </summary>
        private void SendOutfittingData_i()
        {
            try
            {
                MessageHeader header;
                String schema;
                StringBuilder outfittingMessage = new StringBuilder();

                // fill the header
                header = new MessageHeader()
                {
                    SoftwareName        = "ED-IBE (API)",
                    SoftwareVersion     = VersionHelper.Parts(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version, 3),
                    GatewayTimestamp    = DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + DateTime.Now.ToString("zzz", CultureInfo.InvariantCulture),
                    UploaderID          = UserIdentification()
                };

                // fill the schema : test or real ?
                if(Program.DBCon.getIniValue(IBE.EDDN.EDDNView.DB_GROUPNAME, "Schema", "Real", false) == "Test")
                    schema = "http://schemas.elite-markets.net/eddn/outfitting/1/test";
                else
                    schema = "http://schemas.elite-markets.net/eddn/outfitting/1";

                // create full message
                outfittingMessage.Append(String.Format("{{" +
                                                       " \"header\" : {0}," +
                                                       " \"$schemaRef\": \"{1}\","+
                                                       " {2}" +
                                                       "}}", 
                                                       JsonConvert.SerializeObject(header, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore }),
                                                       schema,
                                                       _Send_Outfitting.Dequeue().ToString()));


                using (var client = new WebClient())
                {
                    try
                    {
                        Debug.Print(outfittingMessage.ToString());

                        client.UploadString("http://eddn-gateway.elite-markets.net:8080/upload/", "POST", outfittingMessage.ToString());
                    }
                    catch (WebException ex)
                    {
                        _logger.Log("Error uploading json (outfitting)", true);
                        _logger.Log(ex.ToString(), true);
                        _logger.Log(ex.Message, true);
                        _logger.Log(ex.StackTrace, true);
                        if (ex.InnerException != null)
                            _logger.Log(ex.InnerException.ToString(), true);

                        using (WebResponse response = ex.Response)
                        {
                            using (Stream data = response.GetResponseStream())
                            {
                                if (data != null)
                                {
                                    StreamReader sr = new StreamReader(data);
                                    MsgBox.Show(sr.ReadToEnd(), "Error while uploading to EDDN (outfitting)");
                                }
                            }
                        }
                    }
                    finally
                    {
                        client.Dispose();
                    }
                }

            }
            catch (Exception ex)
            {
                _logger.Log("Error uploading Json (outfitting)", true);
                _logger.Log(ex.ToString(), true);
                _logger.Log(ex.Message, true);
                _logger.Log(ex.StackTrace, true);
                if (ex.InnerException != null)
                    _logger.Log(ex.InnerException.ToString(), true);

                cErr.processError(ex, "Error in EDDN-Sending-Thread (outfitting)");
            }

        }

        /// <summary>
        /// internal send routine for registered data:
        /// It's called by the delay-timer "_SendDelayTimer_Commodity"
        /// </summary>
        private void SendShipyardData_i()
        {
            try
            {
                MessageHeader header;
                String schema;
                StringBuilder shipyardMessage = new StringBuilder();

                // fill the header
                header = new MessageHeader()
                {
                    SoftwareName        = "ED-IBE (API)",
                    SoftwareVersion     = VersionHelper.Parts(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version, 3),
                    GatewayTimestamp    = DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + DateTime.Now.ToString("zzz", CultureInfo.InvariantCulture),
                    UploaderID          = UserIdentification()
                };

                // fill the schema : test or real ?
                if(Program.DBCon.getIniValue(IBE.EDDN.EDDNView.DB_GROUPNAME, "Schema", "Real", false) == "Test")
                    schema = "http://schemas.elite-markets.net/eddn/shipyard/1/test";
                else
                    schema = "http://schemas.elite-markets.net/eddn/shipyard/1";

                // create full message
                shipyardMessage.Append(String.Format("{{" +
                                                       " \"header\" : {0}," +
                                                       " \"$schemaRef\": \"{1}\","+
                                                       " {2}" +
                                                       "}}", 
                                                       JsonConvert.SerializeObject(header, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore }),
                                                       schema,
                                                       _Send_Shipyard.Dequeue().ToString()));


                using (var client = new WebClient())
                {
                    try
                    {
                        Debug.Print(shipyardMessage.ToString());

                        client.UploadString("http://eddn-gateway.elite-markets.net:8080/upload/", "POST", shipyardMessage.ToString());
                    }
                    catch (WebException ex)
                    {
                        _logger.Log("Error uploading json (shipyard)", true);
                        _logger.Log(ex.ToString(), true);
                        _logger.Log(ex.Message, true);
                        _logger.Log(ex.StackTrace, true);
                        if (ex.InnerException != null)
                            _logger.Log(ex.InnerException.ToString(), true);

                        using (WebResponse response = ex.Response)
                        {
                            using (Stream data = response.GetResponseStream())
                            {
                                if (data != null)
                                {
                                    StreamReader sr = new StreamReader(data);
                                    MsgBox.Show(sr.ReadToEnd(), "Error while uploading to EDDN (shipyard)");
                                }
                            }
                        }
                    }
                    finally
                    {
                        client.Dispose();
                    }
                }

            }
            catch (Exception ex)
            {
                _logger.Log("Error uploading Json (shipyard)", true);
                _logger.Log(ex.ToString(), true);
                _logger.Log(ex.Message, true);
                _logger.Log(ex.StackTrace, true);
                if (ex.InnerException != null)
                    _logger.Log(ex.InnerException.ToString(), true);

                cErr.processError(ex, "Error in EDDN-Sending-Thread (shipyard)");
            }

        }


        /// <summary>
        /// timer routine for sending all registered commoditydata to EDDN
        /// </summary>
        /// <param name="source"></param>
        /// <param name="e"></param>
        private void SendDelayTimerCommodity_Elapsed(object source, System.Timers.ElapsedEventArgs e)
        {
            try
            {

                // it's time to start the EDDN transmission
                _Spool2EDDN_Commodity = new Thread(new ThreadStart(SendMarketData_i));
                _Spool2EDDN_Commodity.Name = "Spool2EDDN Comodity";
                _Spool2EDDN_Commodity.IsBackground = true;

                _Spool2EDDN_Commodity.Start();

            }
            catch (Exception ex)
            {
                cErr.processError(ex, "Error while sending EDDN data (commodity)");
            }
        }

        /// <summary>
        /// timer routine for sending all registered data to EDDN
        /// </summary>
        /// <param name="source"></param>
        /// <param name="e"></param>
        private void SendDelayTimerOutfitting_Elapsed(object source, System.Timers.ElapsedEventArgs e)
        {
            try
            {

                // it's time to start the EDDN transmission
                _Spool2EDDN_Outfitting = new Thread(new ThreadStart(SendOutfittingData_i));
                _Spool2EDDN_Outfitting.Name = "Spool2EDDN Outfitting";
                _Spool2EDDN_Outfitting.IsBackground = true;

                _Spool2EDDN_Outfitting.Start();

            }
            catch (Exception ex)
            {
                cErr.processError(ex, "Error while sending EDDN data (outfitting)");
            }
        }

        /// <summary>
        /// timer routine for sending all registered data to EDDN
        /// </summary>
        /// <param name="source"></param>
        /// <param name="e"></param>
        private void SendDelayTimerShipyard_Elapsed(object source, System.Timers.ElapsedEventArgs e)
        {
            try
            {

                // it's time to start the EDDN transmission
                _Spool2EDDN_Shipyard = new Thread(new ThreadStart(SendShipyardData_i));
                _Spool2EDDN_Shipyard.Name = "Spool2EDDN Shipyard";
                _Spool2EDDN_Shipyard.IsBackground = true;

                _Spool2EDDN_Shipyard.Start();

            }
            catch (Exception ex)
            {
                cErr.processError(ex, "Error while sending EDDN data (shipyard)");
            }
        }

        /// <summary>
        /// checks and gets the EDDN id
        /// </summary>
        private String UserIdentification()
        {
            String retValue = "";
            String userName = "";

            try
            {
                retValue = Program.DBCon.getIniValue<String>(IBE.EDDN.EDDNView.DB_GROUPNAME, "UserID");

                if(String.IsNullOrEmpty(retValue))
                {
                    retValue = Guid.NewGuid().ToString();
                    Program.DBCon.setIniValue(IBE.EDDN.EDDNView.DB_GROUPNAME, "UserID", retValue);
                }
                    
                if (Program.DBCon.getIniValue(IBE.EDDN.EDDNView.DB_GROUPNAME, "Identification", "useUserName") == "useUserName")
                {
                    userName = Program.DBCon.getIniValue<String>(IBE.EDDN.EDDNView.DB_GROUPNAME, "UserName");

                    if (String.IsNullOrEmpty(userName))
                        Program.DBCon.setIniValue(IBE.EDDN.EDDNView.DB_GROUPNAME, "Identification", "useUserID");
                    else
                        retValue = userName;
                }

                return userName;
            }
            catch (Exception ex)
            {
                throw new Exception("Error while checking the EDDN id", ex);
            }
        }

        public void SendingReset()
        {
            m_ID_of_Commodity_Station  =  new Tuple<String, DateTime>("", new DateTime());
            m_ID_of_Outfitting_Station =  new Tuple<String, DateTime>("", new DateTime());
            m_ID_of_Shipyard_Station   = new Tuple<String, DateTime>("", new DateTime());
        }
#endregion
    }
}
