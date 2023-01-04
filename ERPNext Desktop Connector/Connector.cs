﻿using ERPNext_Desktop_Connector.Commands;
using ERPNext_Desktop_Connector.Events;
using ERPNext_Desktop_Connector.Handlers;
using ERPNext_Desktop_Connector.Objects;
using ERPNext_Desktop_Connector.Options;
using Sage.Peachtree.API;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;

namespace ERPNext_Desktop_Connector
{
    public class Connector
    {
        private const string CompanyName = "Electro-Comp Tape & Reel Services, LLC";
        private string ApplicationId = Properties.Settings.Default.ApplicationId;
        private string CompanyFile = Properties.Settings.Default.File;
        private bool _canRequest = true;
        private Timer _timer;
        private ILogger Logger { get; set; }
        private static PeachtreeSession Session { get; set; }
        public static Company Company { get; set; }
        public ConcurrentQueue<object> Queue = new ConcurrentQueue<object>();

        public event EventHandler ConnectorStarted;
        public event EventHandler ConnectorStopped;
        public event EventHandler<EventDataArgs> PeachtreeInformation;
        public event EventHandler<EventDataArgs> ConnectorInformation;
        public event EventHandler<EventDataArgs> LoggedInStateChange;
        private void OpenCompany(CompanyIdentifier companyId)
        {
            // Request authorization from Sage 50 for our third-party application.
            try
            {
                OnPeachtreeInformation(EventData("Requesting access to your company in Sage 50..."));
                var presentAccessStatus = Session.VerifyAccess(companyId);
                var authorizationResult = presentAccessStatus == AuthorizationResult.Granted ? presentAccessStatus : Session.RequestAccess(companyId);

                // Check to see we were granted access to Sage 50 company, if so, go ahead and open the company.
                if (authorizationResult == AuthorizationResult.Granted)
                {
                    Company = Session.Open(companyId);
                    Logger.Information("Authorization granted");
                    OnPeachtreeInformation(EventData("Access to your company was granted"));
                    OnLoggedInStateChanged(EventData("Logged In"));
                }
                else // otherwise, display a message to user that there was insufficient access.
                {
                    Logger.Error("Authorization request was not successful - {0}. Will retry.", authorizationResult);
                    OnPeachtreeInformation(EventData($"Authorization status is {authorizationResult}. Still waiting for authorization to access your company..."));
                    OnLoggedInStateChanged(EventData("Logged Out. Waiting for Sage 50 authorization"));
                }
            }
            catch (Sage.Peachtree.API.Exceptions.LicenseNotAvailableException e)
            {
                Logger.Debug(e, e.Message);
                OnPeachtreeInformation(EventData("Could not open your company because it seems like you do not have a license."));
            }
            catch (Sage.Peachtree.API.Exceptions.RecordInUseException e)
            {
                Logger.Debug(e, e.Message);
                OnPeachtreeInformation(EventData("Could not open your company as one or more records are in use."));
            }
            catch (Sage.Peachtree.API.Exceptions.AuthorizationException e)
            {
                Logger.Debug(e, e.Message);
                OnPeachtreeInformation(EventData($"Could not open your company as authorization failed. {e.Message}"));
            }
            catch (Sage.Peachtree.API.Exceptions.PeachtreeException e)
            {
                Logger.Debug(e, e.Message);
                OnPeachtreeInformation(EventData($"Could not open your company due to a Sage 50 internal error. {e.Message}"));
            }
            catch (Exception e)
            {
                Logger.Debug(e, e.Message);
                OnPeachtreeInformation(EventData($"Something went wrong. {e.Message}"));
            }
        }

        private void CloseCompany()
        {
            Company?.Close();
            OnPeachtreeInformation(EventData("Company was closed"));
        }

        private void OpenSession(string appKeyId)
        {
            try
            {
                if (Session != null)
                {
                    CloseSession();
                }

                // create new session                                
                Session = new PeachtreeSession();

                // start the new session
                Session.Begin(appKeyId);
                OnPeachtreeInformation(EventData("Sage 50 session has started and will try to get authorization next"));
                OnLoggedInStateChanged(EventData("Log in not yet confirmed"));
            }
            catch (Sage.Peachtree.API.Exceptions.ApplicationIdentifierExpiredException e)
            {
                Logger.Debug(e, "Your application identifier has expired.");
                OnPeachtreeInformation(EventData("Your application identifier has expired"));
                OnLoggedInStateChanged(EventData("Logged Out"));
            }
            catch (Sage.Peachtree.API.Exceptions.ApplicationIdentifierRejectedException e)
            {
                Logger.Debug(e, "Your application identifier was rejected.");
                OnPeachtreeInformation(EventData("Your application identifier was rejected."));
                OnLoggedInStateChanged(EventData("Logged Out"));
            }
            catch (Sage.Peachtree.API.Exceptions.PeachtreeException e)
            {
                Logger.Debug(e, e.Message);
                OnPeachtreeInformation(EventData(e.Message));
                OnLoggedInStateChanged(EventData("Logged Out"));
            }
            catch (Exception e)
            {
                Logger.Debug(e, e.Message);
                OnPeachtreeInformation(EventData(e.Message));
                OnLoggedInStateChanged(EventData("Logged Out"));
            }
        }

        private EventDataArgs EventData(string v)
        {
            var args = new EventDataArgs
            {
                Text = v
            };
            return args;
        }

        // Closes current Sage 50 Session
        //
        private void CloseSession()
        {
            if (Session != null && Session.SessionActive)
            {
                Session.End();
                Session = null;
                OnPeachtreeInformation(EventData("Sage 50 session ended"));
                OnLoggedInStateChanged(EventData("Logged out"));
            } else if (Session != null)
            {
                Session = null;
            }
        }

        public Connector()
        {
            SetupLogger();
            Logger.Information("initializing object");
        }

        private void SetupLogger()
        {
            const string path = @"%PROGRAMDATA%\ERPNextConnector\Logs\log-.txt";
            var logFilePath = Environment.ExpandEnvironmentVariables(path);
            Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day)
                .CreateLogger();
        }

        public void OnStart()
        {
            _canRequest = true;
            Logger.Information("Service started");
            Logger.Debug("State = {0}", _canRequest);
            OpenSession(ApplicationId);
            DiscoverAndOpenCompany();
            // CheckLoggedInStatus();
            StartTimer();
        }

        public void ManualStart()
        {
            _canRequest = true;
            Logger.Information("Manual sync started");
            OpenSession(ApplicationId);
            SyncAsync();
        }

        private void ClearQueue()
        {
            Logger.Information("Version {@Version}", Settings.Version);
            Logger.Information("Now attempting to treat queued documents");
            if (Queue.IsEmpty || Company == null || Company.IsClosed) return;
            var handler = new DocumentTypeHandler(Company, Logger);
            while (Queue.TryDequeue(out var document) && Session.SessionActive)
            {
                handler.Handle(document);
                OnConnectorInformation(EventData("Busy"));
            }
            OnConnectorInformation(EventData("No more documents to process at the moment"));
        }


        private void StartTimer()
        {
            _timer = new Timer
            {
                Interval = Convert.ToDouble(Properties.Settings.Default.PollingInterval) * 60000
            };
            _timer.Elapsed += OnTimer;
            _timer.Start();
            OnConnectorStarted(EventArgs.Empty);
            Logger.Information("Timer started");
            Logger.Information("Timer interval is {0} minutes", _timer.Interval / 60000);
            OnPeachtreeInformation(EventData($"Documents will be synchronized in {_timer.Interval / 60000} minutes"));
        }

        /**
         * Starts the process of getting documents from ERPNext and pushing
         * them into Sage 50.
         * When there is no active session or the service cannot connect to
         * the company, `OnTimer` will fail silently.
         */
        private void OnTimer(object sender, ElapsedEventArgs e)
        {
            var startTime = Properties.Settings.Default.SyncStartTime;
            var endTime = Properties.Settings.Default.SyncStopTime;
            var isAutoMode = Properties.Settings.Default.AutomaticSync;

            Logger.Information("===========================================================");
            Logger.Information($"Connector is ready: {_canRequest}");
            Logger.Information($"Auto mode: {isAutoMode}");
            Logger.Information($"Auto time range: {IsWithinTimeRange(startTime, endTime)}");
            Logger.Information("===========================================================");

            if (!_canRequest || (isAutoMode && !IsWithinTimeRange(startTime, endTime)))
            {
                Logger.Debug("Service cannot request: {0}, {1}", _canRequest, DateTime.Now.Hour);
                var message = _canRequest ? $"Connector is idling till {startTime.ToShortTimeString()}" : "Connector is stopped";
                OnConnectorInformation(EventData(message));
                return;
            }

            Sync();
        }

        public void Sync(bool manual=false)
        {
            OnConnectorInformation(EventData($"Synchronization in progress..."));
            if (Company == null || Company.IsClosed)
            {
                DiscoverAndOpenCompany();
            }
            else
            {
                Logger.Debug("Company is null: {0}; Company is closed: {1}", Company == null, Company?.IsClosed);
            }

            if (Session != null && Session.SessionActive && Company != null)
            {
                if (!Company.IsClosed && Queue.IsEmpty)
                {
                    GetDocumentsThenProcessQueue(manual);
                }
                else if (!Company.IsClosed)
                {
                    Logger.Debug("Queue is not yet empty. Queue will be reset. Consider increasing the poll interval.");
                    Queue = new ConcurrentQueue<object>();
                    OnConnectorInformation(EventData("New documents are available so document queue has been reset."));
                    GetDocumentsThenProcessQueue(manual);
                }
                else
                {
                    OnConnectorInformation(EventData("Could not fetch data because company is closed"));
                    Logger.Debug("Session is null: {0}, Session is active: {1}, Company is null: {2}", Session == null, Session?.SessionActive, Company == null);
                }
            }
            else
            {
                Logger.Debug("Session is initialized: {0}", Session != null);
                Logger.Debug("Session is active: {0}", Session != null && Session.SessionActive);
                Logger.Debug("Company is initialized: {0}", Company != null);
            }
        }

        public Task SyncAsync()
        {
            return Task.Run(() => Sync(true));
        }

        private bool IsWithinTimeRange(DateTime startTime, DateTime endTime)
        {
            var now = DateTime.Now;
            if (now.Hour == endTime.Hour && now.Minute <= endTime.Minute)
            {
                return true;
            }
            if (now.Hour == startTime.Hour && now.Minute >= startTime.Minute)
            {
                return true;
            }
            if (startTime.Hour < endTime.Hour)
            {
                return startTime.Hour < now.Hour && now.Hour < endTime.Hour;
            }
            if (startTime.Hour > endTime.Hour)
            {
                return startTime.Hour < now.Hour || now.Hour < endTime.Hour;
            }
            return false;
        }

        private void GetDocumentsThenProcessQueue(bool manual)
        {
            GetDocuments(manual);
            ClearQueue();
        }

        private CompanyIdentifier DiscoverCompany()
        {
            bool Predicate(CompanyIdentifier c) { return string.IsNullOrEmpty(CompanyFile) ? c.CompanyName.ToLower() == CompanyName.ToLower() : c.Path == CompanyFile.ToLower(); }
            try
            {
                var companies = Session.CompanyList();
                var company = companies.Find(Predicate);
                return company;
            }
            catch (Exception e)
            {
                CompanyIdentifier company = null;
                OnPeachtreeInformation(EventData($"Something went wrong. {e.Message}."));
                return company;
            }
        }

        private void DiscoverAndOpenCompany()
        {
            var company = DiscoverCompany();
            if (company != null)
            {
                OpenCompany(company);
                // CheckLoggedInStatus(company);
            } else
            {
                OnPeachtreeInformation(EventData("No company was found."));
            }
        }

        /**
         * Pull documents from ERPNext and queue them for processing.
         * The documents pulled are Sales Orders and Purchase Orders, in that order
         */
        private void GetDocuments(bool manual)
        {
            if (!IsConnectedToInternet())
            {
                Logger.Information("It seems this computer is not connected to the internet");
                OnLoggedInStateChanged(EventData("Logged in but this computer might not connected to the internet"));
            } else
            {
                Logger.Information("It seems this computer is connected to the internet");
                OnPeachtreeInformation(EventData("Internet seems to be ok..."));
            }
            Logger.Information("Attempting to retrieve purchase order data from ERP");
            QueuePurchaseOrders();
            Logger.Information("Attempting to retrieve sales order data from ERP");
            QueueSalesOrders(manual);
            Logger.Information("Attempting to retrieve sales invoice data from ERP");
            QueueSalesInvoices(manual);
        }

        private void QueueSalesInvoices(bool manual)
        {
            var url = manual ? $"{Properties.Settings.Default.ServerAddress}/api/method/electro_erpnext.utilities.sales_invoice.get_many_sales_invoices_for_sage?manual=1" : $"{Properties.Settings.Default.ServerAddress}/api/method/electro_erpnext.utilities.sales_invoice.get_sales_invoices_for_sage";
            var salesInvoiceCommand = new SalesInvoiceCommand(serverUrl: url);
            var salesInvoices = salesInvoiceCommand.Execute();
            Logger.Debug($"ResponseStatus: {salesInvoices.ResponseStatus}, ErrorMessage: {salesInvoices.ErrorMessage}, ContentLength: {salesInvoices.ContentLength}");
            if (salesInvoices == null || salesInvoices.Data?.Message == null)
            {
                OnConnectorInformation(EventData("The server did not return data successfully"));
                return;
            }
            OnConnectorInformation(EventData($"ERPNext sent {salesInvoices?.Data?.Message?.Count} sales invoices."));
            SendToQueue(salesInvoices.Data);
        }

        private void QueueSalesOrders(bool manual)
        {
            var url = manual ? $"{Properties.Settings.Default.ServerAddress}/api/method/electro_erpnext.utilities.sales_order.get_many_sales_orders_for_sage" : $"{Properties.Settings.Default.ServerAddress}/api/method/electro_erpnext.utilities.sales_order.get_sales_orders_for_sage";
            var salesOrderCommand = new SalesOrderCommand(serverUrl: url);
            var salesOrders = salesOrderCommand.Execute();
            Logger.Debug($"ResponseStatus: {salesOrders.ResponseStatus}, ErrorMessage: {salesOrders.ErrorMessage}, ContentLength: {salesOrders.ContentLength}");
            if (salesOrders == null || salesOrders.Data?.Message == null)
            {
                OnConnectorInformation(EventData("The server did not return data successfully"));
                return;
            }
            OnConnectorInformation(EventData($"ERPNext sent {salesOrders.Data.Message.Count} sales orders."));
            SendToQueue(salesOrders.Data);
        }

        private void QueuePurchaseOrders()
        {
            var purchaseOrderCommand = new PurchaseOrderCommand(serverUrl: $"{Properties.Settings.Default.ServerAddress}/api/method/electro_erpnext.utilities.purchase_order.get_purchase_orders_for_sage");
            var purchaseOrders = purchaseOrderCommand.Execute();
            Logger.Debug($"ResponseStatus: {purchaseOrders.ResponseStatus}, ErrorMessage: {purchaseOrders.ErrorMessage}, ContentLength: {purchaseOrders.ContentLength}");

            if (purchaseOrders == null || purchaseOrders?.Data?.Message == null)
            {
                OnConnectorInformation(EventData("The server did not return data successfully"));
                return;
            }
            var number = purchaseOrders?.Data?.Message?.Count;
            OnConnectorInformation(EventData($"ERPNext sent {(number == null ? 0 : number)} purchase orders."));
            SendToQueue(purchaseOrders.Data);
        }

        /**
         * Push documents to internal queue
         */
        private void SendToQueue(SalesOrderResponse response)
        {
            if (response?.Message == null) return;
            foreach (var item in response.Message)
            {
                this.Queue.Enqueue(item);
            }

        }

        private void SendToQueue(PurchaseOrderResponse response)
        {
            if (response?.Message == null) return;
            foreach (var item in response.Message)
            {
                this.Queue.Enqueue(item);
            }

        }

        private void SendToQueue(SalesInvoiceResponse response)
        {
            if (response?.Message == null) return;
            foreach (var item in response.Message)
            {
                this.Queue.Enqueue(item);
            }
        }

        public void OnStop()
        {
            _canRequest = false;
            CloseCompany();
            CloseSession();
            StopTimer();
            Logger.Debug("Timer stopped");
        }

        private void StopTimer()
        {
            _timer?.Stop();
            _timer?.Close();
            _timer = null;
            OnConnectorStopped(EventArgs.Empty);
            OnConnectorInformation(EventData("Connector has cleaned up its connection to Sage 50 and is now idling"));
        }

        protected virtual void OnConnectorStarted(EventArgs e)
        {
            EventHandler eventHandler = ConnectorStarted;
            eventHandler?.Invoke(this, e);
        }

        protected virtual void OnConnectorInformation(EventDataArgs e)
        {
            var eventHandler = ConnectorInformation;
            eventHandler?.Invoke(this, e);
        }

        protected virtual void OnConnectorStopped(EventArgs e)
        {
            var eventHandler = ConnectorStopped;
            eventHandler?.Invoke(this, e);
        }

        protected virtual void OnPeachtreeInformation(EventDataArgs e)
        {
            var eventHandler = PeachtreeInformation;
            eventHandler?.Invoke(this, e);
        }

        protected virtual void OnLoggedInStateChanged(EventDataArgs e)
        {
            var eventHandler = LoggedInStateChange;
            eventHandler?.Invoke(this, e);
        }

        // copied from https://www.c-sharpcorner.com/uploadfile/nipuntomar/check-internet-connection/
        public bool IsConnectedToInternet()
        {
            string host = "google.com";
            bool result = false;
            Ping p = new Ping();
            try
            {
                PingReply reply = p.Send(host, 3000);
                if (reply.Status == IPStatus.Success)
                    return true;
                Logger.Debug($"Status returned from ping was {reply.Status}");
            }
            catch { }
            return result;
        }
    }
}
