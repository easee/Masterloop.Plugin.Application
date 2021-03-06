﻿using Masterloop.Core.Types.Base;
using Masterloop.Core.Types.Commands;
using Masterloop.Core.Types.LiveConnect;
using Masterloop.Core.Types.Observations;
using Masterloop.Core.Types.Pulse;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Masterloop.Plugin.Application
{
    public class MasterloopLiveConnection : IMasterloopLiveConnection
    {
        #region PrivateMembers
        private MasterloopServerConnection _apiServerConnection;
        private LiveConnectionDetails _liveConnectionDetails;
        private ConnectionFactory _connectionFactory;
        private IConnection _connection;
        private IModel _model;
        private EventingBasicConsumer _consumer;
        private string _consumerTag;
        private List<ObservationSubscription<Observation>> _observationSubscriptions;
        private List<ObservationSubscription<byte[]>> _binarySubscriptions;
        private List<ObservationSubscription<BooleanObservation>> _booleanSubscriptions;
        private List<ObservationSubscription<DoubleObservation>> _doubleSubscriptions;
        private List<ObservationSubscription<IntegerObservation>> _integerSubscriptions;
        private List<ObservationSubscription<PositionObservation>> _positionSubscriptions;
        private List<ObservationSubscription<StringObservation>> _stringSubscriptions;
        private List<ObservationSubscription<StatisticsObservation>> _statisticsSubscriptions;
        private List<CommandSubscription<Command>> _commandSubscriptions;
        private List<CommandSubscription<CommandResponse>> _commandResponseSubscriptions;
        private List<PulseSubscription> _pulseSubscriptions;
        private ushort _heartbeatInterval = 60;
        private bool _disposed;
        private List<LiveAppRequest> _liveRequests;
        private readonly object _modelLock;
        private Dictionary<int, DataType> _observationType;
        private ConcurrentQueue<BasicDeliverEventArgs> _queue;
        private string _localAddress;
        private bool _transactionOpen;
        private string _lastFetchedMessageRoutingKey;
        private string _lastFetchedMessageBody;
        #endregion

        #region Configuration
        /// <summary>
        /// True ignores any SSL certificate errors, False does not ignore any SSL certificate errors.
        /// </summary>
        public bool IgnoreSslCertificateErrors { get; set; } = false;

        /// <summary>
        /// Specifies the requested heartbeat interval in seconds. Must be within the range of [60, 3600] seconds. Use 0 to disable heartbeats.
        /// More info can be found here: https://www.rabbitmq.com/heartbeats.html
        /// </summary>
        public ushort HeartbeatInterval
        {
            get
            {
                return _heartbeatInterval;
            }
            set
            {
                if (value == 0 || (value >= 60 && value <= 3600))
                {
                    _heartbeatInterval = value;
                }
                else
                {
                    throw new ArgumentOutOfRangeException("HeartbeatInterval", value, "Heartbeat interval must be between 60 and 3600 seconds (or 0 for disabled).");
                }
            }
        }

        /// <summary>
        /// Network timeout in seconds (default: 30).
        /// </summary>
        public int Timeout { get; set; } = 30;

        /// <summary>
        /// Application metadata used in server api interactions for improved tracability (optional).
        /// </summary>
        public ApplicationMetadata Metadata { get; set; }

        /// <summary>
        /// Live connection details.
        /// </summary>
        public LiveConnectionDetails ConnectionDetails
        {
            get
            {
                return _liveConnectionDetails;
            }
        }

        /// <summary>
        /// Live connection key.
        /// </summary>
        public string ConnectionKey
        {
            get
            {
                if (_liveConnectionDetails != null && _liveConnectionDetails.QueueName != null)
                {
                    // <userid>@@@<key>.Q
                    string[] subStrings = _liveConnectionDetails.QueueName.Split(new[] { "@@@", "." }, StringSplitOptions.None);
                    if (subStrings != null && subStrings.Length == 3)
                    {
                        return subStrings[1];
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Set to True for automatic callbacks to be called (default), or False to control callback using Fetch method.
        /// </summary>
        public bool UseAutomaticCallbacks { get; set; } = true;

        /// <summary>
        /// Set to True to quickly acknowledge all incoming messages (default), or False for acknowledge only messages with registered handlers.
        /// </summary>
        public bool UseAutomaticAcknowledgement { get; set; } = true;

        /// <summary>
        /// Set to True to send messages immediatelly (default), or False when using transactions.
        /// </summary>
        public bool UseAtomicTransactions { get; set; } = true;

        /// <summary>
        /// Prefetch count, must be set before opening connection. Default is 20.
        /// More info can be found here: https://www.rabbitmq.com/consumer-prefetch.html
        /// </summary>
        public int PrefetchCount { get; set; } = 20;
        #endregion

        #region State
        /// <summary>
        /// Returns number of queued messages.
        /// </summary>
        public int QueueCount
        {
            get
            {
                return _queue.Count;
            }
        }

        /// <summary>
        /// Last error message as text string in english.
        /// </summary>
        public string LastErrorMessage { get; set; }

        /// <summary>
        /// Last fetch message routing key as text string.
        /// </summary>
        public string LastFetchedMessageRoutingKey
        {
            get
            {
                return _lastFetchedMessageRoutingKey;
            }
        }

        /// <summary>
        /// Last fetched message body as text string.
        /// </summary>
        public string LastFetchedMessageBody
        {
            get
            {
                return _lastFetchedMessageBody;
            }
        }
        #endregion

        #region LifeCycle
        /// <summary>
        /// Constructs a new live connection using MCS credentials.
        /// </summary>
        /// <param name="hostName">Host to connect to, typically "api.masterloop.net".</param>
        /// <param name="username">MCS username.</param>
        /// <param name="password">MCSLogin password.</param> 
        /// <param name="useEncryption">True if using encryption, False if not using encryption.</param>
        public MasterloopLiveConnection(string hostName, string username, string password, bool useEncryption)
        {
            _modelLock = new object();
            Init();
            _apiServerConnection = new MasterloopServerConnection(hostName, username, password, useEncryption);
            _observationType = new Dictionary<int, DataType>();
            _queue = new ConcurrentQueue<BasicDeliverEventArgs>();
            _localAddress = GetLocalIPAddress();
            _transactionOpen = false;

            // Set default metadata to calling application.
            Assembly calling = Assembly.GetCallingAssembly();
            System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(calling.Location);
            Metadata = new ApplicationMetadata()
            {
                Application = calling.GetName().Name,
                Reference = fvi.FileVersion
            };
        }

        /// <summary>
        /// Constructs a new live connection object using live message server credentials.
        /// </summary>
        /// <param name="liveConnectionDetails">Live message server connection details.</param>
        public MasterloopLiveConnection(LiveConnectionDetails liveConnectionDetails)
        {
            _modelLock = new object();
            Init();
            _liveConnectionDetails = liveConnectionDetails;
            _observationType = new Dictionary<int, DataType>();
            _queue = new ConcurrentQueue<BasicDeliverEventArgs>();
            _localAddress = GetLocalIPAddress();
            _transactionOpen = false;

            // Set default metadata to calling application.
            Assembly calling = Assembly.GetCallingAssembly();
            System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(calling.Location);
            Metadata = new ApplicationMetadata()
            {
                Application = calling.GetName().Name,
                Reference = fvi.FileVersion
            };
        }

        /// <summary>
        /// Destroys this object.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_disposed) return;
                if (disposing)
                {
                    Disconnect();
                }
                _disposed = true;
            }
        }
        #endregion

        #region Connection
        /// <summary>
        /// Connects to live server using default devices. Use if object is constructed using LiveConnectionDetails object. Does not offer re-connect.
        /// </summary>
        /// <returns>True if connection was successful, False otherwise. Note: If the initial call to Connect fails, re-connection will not be started.</returns>
        public bool Connect()
        {
            Disconnect();  // Remove any existing connection objects

            return OpenConnection();
        }

        /// <summary>
        /// Connects to specified devices or templates. Can only be used if object has been constructed using MCS credentials due to built-in re-connect feature.
        /// </summary>
        /// <param name="liveRequests">Array of LiveAppRequest objects containing device or template connection arguments.</param>
        /// <returns>True if connection was successful, False otherwise. Note: If the initial call to Connect fails, re-connection will not be started.</returns>
        public bool Connect(LiveAppRequest[] liveRequests)
        {
            if (Connect())  // Recycle existing connection if possible
            {
                return true;
            }
            else if (_apiServerConnection != null)
            {
                // Recycling failed, request a new live connection
                _liveRequests = new List<LiveAppRequest>(liveRequests);
                _apiServerConnection.Timeout = Timeout;
                _liveConnectionDetails = _apiServerConnection.RequestLiveConnection(_liveRequests.ToArray());

                return OpenConnection();
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Connects to specified devices or templates. Can only be used if object has been constructed using MCS credentials due to built-in re-connect feature.
        /// </summary>
        /// <param name="liveRequests">Array of LiveAppRequest objects containing device or template connection arguments.</param>
        /// <returns>True if connection was successful, False otherwise. Note: If the initial call to Connect fails, re-connection will not be started.</returns>
        public async Task<bool> ConnectAsync(LiveAppRequest[] liveRequests)
        {
            if (Connect())  // Recycle existing connection if possible
            {
                return true;
            }
            else if (_apiServerConnection != null)
            {
                // Recycling failed, request a new live connection
                _liveRequests = new List<LiveAppRequest>(liveRequests);
                _apiServerConnection.Timeout = Timeout;
                _liveConnectionDetails = await _apiServerConnection.RequestLiveConnectionAsync(_liveRequests.ToArray());

                return OpenConnection();
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Disconnects from MCS Live Server. 
        /// </summary>
        public void Disconnect()
        {
            if (_consumer != null)
            {
                lock (_consumer)
                {
                    _consumer.Received -= ConsumerReceived;
                    _consumer = null;
                }
            }

            if (_model != null)
            {
                lock (_modelLock)
                {
                    if (_model.IsOpen)
                    {
                        try
                        {
                            _model.Close(200, "Goodbye");
                        }
                        catch (Exception) { }
                    }
                    _model.Dispose();
                    _model = null;
                    _transactionOpen = false;
                }
            }

            if (_connection != null)
            {
                lock (_connection)
                {
                    if (_connection.IsOpen)
                    {
                        try
                        {
                            _connection.Close();
                        }
                        catch (Exception) { }
                    }
                    _connection.Dispose();
                    _connection = null;
                }
            }

            if (_connectionFactory != null)
            {
                lock (_connectionFactory)
                {
                    _connectionFactory = null;
                }
            }
        }

        /// <summary>
        /// Reports connection status to MCS Live Server.
        /// </summary>
        /// <returns>True if connected, False otherwise.</returns>
        public bool IsConnected()
        {
            if (_connectionFactory == null) return false;
            if (_connection == null) return false;
            if (!_connection.IsOpen) return false;
            if (_model == null) return false;
            lock (_modelLock)
            {
                return _model.IsOpen;
            }
        }

        /// <summary>
        /// Pauses listening for incoming messages.
        /// </summary>
        /// <returns></returns>
        public bool PauseIncoming()
        {
            bool success = false;
            if (_consumer != null && _consumerTag != string.Empty)
            {
                lock (_model)
                {
                    try
                    {
                        _consumer.Received -= ConsumerReceived;
                        _model.BasicCancel(_consumerTag);
                        _consumerTag = string.Empty;
                        success = true;
                    }
                    catch (Exception e)
                    {
                        LastErrorMessage = e.Message;
                    }
                }
            }
            return success;
        }

        /// <summary>
        /// Resumes listening for incoming messages.
        /// </summary>
        /// <returns></returns>
        public bool ResumeIncoming()
        {
            bool success = false;
            if (_consumer != null && _consumerTag != string.Empty)
            {
                lock (_model)
                {
                    try
                    {
                        _consumer.Received += ConsumerReceived;
                        _consumerTag = _model.BasicConsume(_liveConnectionDetails.QueueName, false, _consumer);
                        success = true;
                    }
                    catch (Exception e)
                    {
                        LastErrorMessage = e.Message;
                    }
                }
            }
            return success;
        }

        /// <summary>
        /// Fetch next 1 incoming message in queue and dispatch if event handler is associated with it.
        /// </summary>
        /// <returns>True if message was received, false otherwise.</returns>
        public bool Fetch()
        {
            _lastFetchedMessageRoutingKey = null;
            _lastFetchedMessageBody = null;

            // Queue is empty, nothing to fetch
            if (_queue.IsEmpty)
            {
                return false;
            }

            // Use concurrent dequeueing, return dispatch state if successfull
            if (_queue.TryDequeue(out BasicDeliverEventArgs message))
            {
                try
                {
                    _lastFetchedMessageRoutingKey = message.RoutingKey;
                    _lastFetchedMessageBody = Encoding.UTF8.GetString(message.Body.Span);
                }
                catch (Exception) { }
                return Dispatch(message.RoutingKey, GetMessageHeader(message), message.Body.Span, message.DeliveryTag);
            }

            // Failed to dequeue, probably concurrent dequeue and empty queue, so return false
            return false;
        }
        #endregion

        #region Publish
        /// <summary>
        /// Synchronously publishes a new command and optionally waits for acceptance.
        /// </summary>
        /// <param name="MID">Device identifier or null for all.</param>
        /// <param name="command">Command object.</param>
        public bool SendCommand(string MID, Command command)
        {
            if (UseAtomicTransactions && _transactionOpen)
            {
                throw new ArgumentException("Unable to use atomic transactions when object has an open transaction.");
            }

            if (IsConnected())
            {
                IBasicProperties properties = GetMessageProperties(1);
                if (command.ExpiresAt.HasValue)
                {
                    TimeSpan ts = command.ExpiresAt.Value - DateTime.UtcNow;
                    if (ts.TotalMilliseconds > 0)
                    {
                        properties.Expiration = ts.TotalMilliseconds.ToString("F0");
                    }
                }
                if (this.Metadata != null)
                {
                    AppendMetadata(properties);
                }
                string routingKey = MessageRoutingKey.GenerateDeviceCommandRoutingKey(MID, command.Id, command.Timestamp);
                string json = JsonConvert.SerializeObject(command);
                byte[] body = Encoding.UTF8.GetBytes(json);
                try
                {
                    lock (_modelLock)
                    {
                        _model.BasicPublish(_liveConnectionDetails.ExchangeName, routingKey, true, properties, body);
                    }
                    return true;
                }
                catch (Exception e)
                {
                    LastErrorMessage = e.Message;
                }
            }
            return false;
        }

        /// <summary>
        /// Sends an application pulse to the server for a specified device.
        /// </summary>
        /// <param name="MID">Device identifier or null for all.</param>
        /// <param name="pulseId">Application pulse identifier.</param>
        /// <param name="timestamp">Timestamp in UTC indicating the time of the pulse. null for current time.</param>
        /// <param name="expiryMilliseconds">Expiry time of pulse signal in milli seconds. Use 0 to never expire. Default 300000 (5 minutes).</param>
        /// <returns>True if successful, False otherwise.</returns>
        public bool SendPulse(string MID, int pulseId, DateTime? timestamp, int expiryMilliseconds = 300000)
        {
            if (UseAtomicTransactions && _transactionOpen)
            {
                throw new ArgumentException("Unable to use atomic transactions when object has an open transaction.");
            }

            if (IsConnected())
            {
                if (!timestamp.HasValue)
                {
                    timestamp = DateTime.UtcNow;
                }

                Pulse pulse = new Pulse()
                {
                    Timestamp = timestamp.Value,
                    MID = MID,
                    PulseId = pulseId
                };

                IBasicProperties properties = GetMessageProperties(2);
                if (expiryMilliseconds > 0)
                {
                    properties.Expiration = expiryMilliseconds.ToString("F0");
                }
                string routingKey = MessageRoutingKey.GeneratePulseRoutingKey(pulse.MID, pulse.PulseId);
                string json = JsonConvert.SerializeObject(pulse);
                byte[] body = Encoding.UTF8.GetBytes(json);
                try
                {
                    lock (_modelLock)
                    {
                        _model.BasicPublish(_liveConnectionDetails.ExchangeName, routingKey, false, properties, body);
                    }
                }
                catch (Exception e)
                {
                    LastErrorMessage = e.Message;
                    return false;
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Sends an application pulse to the server for all explicitly connected devices. Device identifies application by PulseId specified in the request array in Connect().
        /// </summary>
        /// <param name="timestamp">Timestamp in UTC indicating the time of the pulse. null for current time.</param>
        /// <param name="expiryMilliseconds">Expiry time of pulse signal in milli seconds. Use 0 to never expire. Default 300000 (5 minutes).</param>
        /// <returns>True if successful, False otherwise.</returns>
        public bool SendPulse(DateTime? timestamp, int expiryMilliseconds = 300000)
        {
            foreach (LiveAppRequest request in _liveRequests)
            {
                if (!request.PulseId.HasValue)
                {
                    throw new ArgumentNullException("LiveAppRequest structure to Connect() must specify a valid PulseId value.");
                }

                if (!SendPulse(request.MID, request.PulseId.Value, timestamp, expiryMilliseconds))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Begins a new multi publish transaction.
        /// </summary>
        /// <returns>True if successful, False otherwise.</returns>
        public bool PublishBegin()
        {
            if (UseAutomaticCallbacks)
            {
                throw new ArgumentException("Unable to use transactions if UseAutomaticCallbacks is set to true.");
            }
            lock (_modelLock)
            {
                try
                {
                    _model.TxSelect();
                    _transactionOpen = true;
                    return true;
                }
                catch (Exception e)
                {
                    LastErrorMessage = e.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// Commit a multi publish transaction.
        /// </summary>
        /// <returns>True if successful, False otherwise.</returns>
        public bool PublishCommit()
        {
            if (UseAutomaticCallbacks)
            {
                throw new ArgumentException("Unable to use transactions if UseAutomaticCallbacks is set to true.");
            }
            lock (_modelLock)
            {
                try
                {
                    _model.TxCommit();
                    _transactionOpen = false;
                    return true;
                }
                catch (Exception e)
                {
                    LastErrorMessage = e.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// Rollback a multi publish transaction.
        /// </summary>
        /// <returns>True if successful, False otherwise.</returns>
        public bool PublishRollback()
        {
            if (UseAutomaticCallbacks)
            {
                throw new ArgumentException("Unable to use transactions if UseAutomaticCallbacks is set to true.");
            }
            lock (_modelLock)
            {
                try
                {
                    _model.TxRollback();
                    _transactionOpen = false;
                    return true;
                }
                catch (Exception e)
                {
                    LastErrorMessage = e.Message;
                    return false;
                }
            }
        }
        #endregion

        #region ObservationSubscription
        /// <summary>
        /// Registers a new callback method that is called when a specified observation is received.
        /// </summary>
        /// <param name="MID">Device identifier or null for all, all devices must be of the same template.</param>
        /// <param name="observationId">Observation identifier.</param>
        /// <param name="observationHandler">Callback method with signature "void Callback(string MID, int observationId, Observation o) { ... }"</param>
        public void RegisterObservationHandler(string MID, int observationId, Action<string, int, Observation> observationHandler, DataType dataType)
        {
            if (dataType != DataType.Binary)
            {
                ObservationSubscription<Observation> observationSubscription = new ObservationSubscription<Observation>(MID, observationId, observationHandler);
                _observationSubscriptions.Add(observationSubscription);
                if (!_observationType.ContainsKey(observationId))
                {
                    _observationType.Add(observationId, dataType);
                }
            }
            else
            {
                throw new ArgumentException($"RegisterObservationHandler does not support data type {dataType}.");
            }
        }

        /// <summary>
        /// Registers a new callback method that is called when a specified binary observation blob is received.
        /// </summary>
        /// <param name="MID">Device identifier or null for all, all devices must be of the same template.</param>
        /// <param name="observationId">Observation identifier.</param>
        /// <param name="observationHandler">Callback method with signature "void Callback(string MID, int observationId, byte[] o) { ... }"</param>
        public void RegisterObservationHandler(string MID, int observationId, Action<string, int, byte[]> observationHandler)
        {
            ObservationSubscription<byte[]> observationSubscription = new ObservationSubscription<byte[]>(MID, observationId, observationHandler);
            _binarySubscriptions.Add(observationSubscription);
            if (!_observationType.ContainsKey(observationId))
            {
                _observationType.Add(observationId, DataType.Binary);
            }
        }

        /// <summary>
        /// Registers a new callback method that is called when a specified boolean observation is received.
        /// </summary>
        /// <param name="MID">Device identifier or null for all, all devices must be of the same template.</param>
        /// <param name="observationId">Observation identifier.</param>
        /// <param name="observationHandler">Callback method with signature "void Callback(string MID, int observationId, BooleanObservation o) { ... }"</param>
        public void RegisterObservationHandler(string MID, int observationId, Action<string, int, BooleanObservation> observationHandler)
        {
            ObservationSubscription<BooleanObservation> observationSubscription = new ObservationSubscription<BooleanObservation>(MID, observationId, observationHandler);
            _booleanSubscriptions.Add(observationSubscription);
            if (!_observationType.ContainsKey(observationId))
            {
                _observationType.Add(observationId, DataType.Boolean);
            }
        }

        /// <summary>
        /// Registers a new callback method that is called when a specified double observation is received.
        /// </summary>
        /// <param name="MID">Device identifier or null for all, all devices must be of the same template.</param>
        /// <param name="observationId">Observation identifier.</param>
        /// <param name="observationHandler">Callback method with signature "void Callback(string MID, int observationId, DoubleObservation o) { ... }"</param>
        public void RegisterObservationHandler(string MID, int observationId, Action<string, int, DoubleObservation> observationHandler)
        {
            ObservationSubscription<DoubleObservation> observationSubscription = new ObservationSubscription<DoubleObservation>(MID, observationId, observationHandler);
            _doubleSubscriptions.Add(observationSubscription);
            if (!_observationType.ContainsKey(observationId))
            {
                _observationType.Add(observationId, DataType.Double);
            }
        }

        /// <summary>
        /// Registers a new callback method that is called when a specified integer observation is received.
        /// </summary>
        /// <param name="MID">Device identifier or null for all, all devices must be of the same template.</param>
        /// <param name="observationId">Observation identifier.</param>
        /// <param name="observationHandler">Callback method with signature "void Callback(string MID, int observationId, IntegerObservation o) { ... }"</param>
        public void RegisterObservationHandler(string MID, int observationId, Action<string, int, IntegerObservation> observationHandler)
        {
            ObservationSubscription<IntegerObservation> observationSubscription = new ObservationSubscription<IntegerObservation>(MID, observationId, observationHandler);
            _integerSubscriptions.Add(observationSubscription);
            if (!_observationType.ContainsKey(observationId))
            {
                _observationType.Add(observationId, DataType.Integer);
            }
        }

        /// <summary>
        /// Registers a new callback method that is called when a specified position observation is received.
        /// </summary>
        /// <param name="MID">Device identifier or null for all, all devices must be of the same template.</param>
        /// <param name="observationId">Observation identifier.</param>
        /// <param name="observationHandler">Callback method with signature "void Callback(string MID, int observationId, PositionObservation o) { ... }"</param>
        public void RegisterObservationHandler(string MID, int observationId, Action<string, int, PositionObservation> observationHandler)
        {
            ObservationSubscription<PositionObservation> observationSubscription = new ObservationSubscription<PositionObservation>(MID, observationId, observationHandler);
            _positionSubscriptions.Add(observationSubscription);
            if (!_observationType.ContainsKey(observationId))
            {
                _observationType.Add(observationId, DataType.Position);
            }
        }

        /// <summary>
        /// Registers a new callback method that is called when a specified string observation is received.
        /// </summary>
        /// <param name="MID">Device identifier or null for all, all devices must be of the same template.</param>
        /// <param name="observationId">Observation identifier.</param>
        /// <param name="observationHandler">Callback method with signature "void Callback(string MID, int observationId, StringObservation o) { ... }"</param>
        public void RegisterObservationHandler(string MID, int observationId, Action<string, int, StringObservation> observationHandler)
        {
            ObservationSubscription<StringObservation> observationSubscription = new ObservationSubscription<StringObservation>(MID, observationId, observationHandler);
            _stringSubscriptions.Add(observationSubscription);
            if (!_observationType.ContainsKey(observationId))
            {
                _observationType.Add(observationId, DataType.String);
            }
        }

        /// <summary>
        /// Registers a new callback method that is called when a specified statistics observation is received.
        /// </summary>
        /// <param name="MID">Device identifier or null for all, all devices must be of the same template.</param>
        /// <param name="observationId">Observation identifier.</param>
        /// <param name="observationHandler">Callback method with signature "void Callback(string MID, int observationId, StatisticsObservation o) { ... }"</param>
        public void RegisterObservationHandler(string MID, int observationId, Action<string, int, StatisticsObservation> observationHandler)
        {
            ObservationSubscription<StatisticsObservation> observationSubscription = new ObservationSubscription<StatisticsObservation>(MID, observationId, observationHandler);
            _statisticsSubscriptions.Add(observationSubscription);
            if (!_observationType.ContainsKey(observationId))
            {
                _observationType.Add(observationId, DataType.Statistics);
            }
        }

        /// <summary>
        /// Removes all callback methods for a specified observation id.
        /// </summary>
        /// <param name="MID">Device identifier or null for all.</param>
        /// <param name="observationId">Observation identifier.</param>
        public void UnregisterObservationHandler(string MID, int observationId)
        {
            RemoveHandler<Observation>(_observationSubscriptions, MID, observationId);
            RemoveHandler<byte[]>(_binarySubscriptions, MID, observationId);
            RemoveHandler<BooleanObservation>(_booleanSubscriptions, MID, observationId);
            RemoveHandler<DoubleObservation>(_doubleSubscriptions, MID, observationId);
            RemoveHandler<IntegerObservation>(_integerSubscriptions, MID, observationId);
            RemoveHandler<PositionObservation>(_positionSubscriptions, MID, observationId);
            RemoveHandler<StringObservation>(_stringSubscriptions, MID, observationId);
            RemoveHandler<StatisticsObservation>(_statisticsSubscriptions, MID, observationId);

            if (ActiveObservationHandlers(observationId) == 0)
            {
                _observationType.Remove(observationId);
            }
        }
        #endregion

        #region CommandSubscription
        /// <summary>
        /// Registers a new callback method that is called when a specified command is received.
        /// </summary>
        /// <param name="MID">Device identifier or null for all.</param>
        /// <param name="commandId">Command identifier.</param>
        /// <param name="commandHandler">Callback method with signature "void Callback(string MID, Command cmd) { ... }"</param>
        public void RegisterCommandHandler(string MID, int commandId, Action<string, Command> commandHandler)
        {
            CommandSubscription<Command> commandSubscription = new CommandSubscription<Command>(MID, commandId, commandHandler);
            _commandSubscriptions.Add(commandSubscription);
        }

        /// <summary>
        /// Removes all callback methods for a specified command.
        /// </summary>
        /// <param name="MID">Device identifier or null for all.</param>
        /// <param name="commandId">Command identifier.</param>
        public void UnregisterCommandHandler(string MID, int commandId)
        {
            List<CommandSubscription<Command>> handlersToRemove = new List<CommandSubscription<Command>>();
            foreach (CommandSubscription<Command> s in _commandSubscriptions)
            {
                if (s.MID == MID && s.CommandId == commandId)
                {
                    handlersToRemove.Add(s);
                }
            }
            for (int i = 0; i < handlersToRemove.Count; i++)
            {
                _commandSubscriptions.Remove(handlersToRemove[i]);
            }
        }

        /// <summary>
        /// Registers a new callback method that is called when a specified command response is received.
        /// </summary>
        /// <param name="MID">Device identifier or null for all.</param>
        /// <param name="commandId">Command identifier.</param>
        /// <param name="commandResponseHandler">Callback method with signature "void Callback(string MID, CommandResponse cmdResponse) { ... }"</param>
        public void RegisterCommandResponseHandler(string MID, int commandId, Action<string, CommandResponse> commandResponseHandler)
        {
            CommandSubscription<CommandResponse> commandSubscription = new CommandSubscription<CommandResponse>(MID, commandId, commandResponseHandler);
            _commandResponseSubscriptions.Add(commandSubscription);
        }

        /// <summary>
        /// Removes all response callback methods for a specified command.
        /// </summary>
        /// <param name="MID">Device identifier or null for all.</param>
        /// <param name="commandId">Command identifier.</param>
        public void UnregisterCommandResponseHandler(string MID, int commandId)
        {
            List<CommandSubscription<CommandResponse>> handlersToRemove = new List<CommandSubscription<CommandResponse>>();
            foreach (CommandSubscription<CommandResponse> s in _commandResponseSubscriptions)
            {
                if (s.MID == MID && s.CommandId == commandId)
                {
                    handlersToRemove.Add(s);
                }
            }
            for (int i = 0; i < handlersToRemove.Count; i++)
            {
                _commandResponseSubscriptions.Remove(handlersToRemove[i]);
            }
        }
        #endregion

        #region PulseSubscription
        /// <summary>
        /// Registers a new callback method that is called when pulse from a specified MID is received.
        /// </summary>
        /// <param name="MID">Hearteat device identifier or null for all.</param>
        /// <param name="pulseHandler">Callback method with signature "void Callback(string MID, int pulseId, Pulse pulse) { ... }"</param>
        public void RegisterPulseHandler(string MID, Action<string, int, Pulse> pulseHandler)
        {
            PulseSubscription pulseSubscription = new PulseSubscription(MID, 0, pulseHandler);
            _pulseSubscriptions.Add(pulseSubscription);
        }

        /// <summary>
        /// Removes all callback methods from a specified device identifier.
        /// </summary>
        /// <param name="MID">Device identifier or null for all.</param>
        public void UnregisterPulseHandler(string MID)
        {
            List<PulseSubscription> handlersToRemove = new List<PulseSubscription>();
            foreach (PulseSubscription s in _pulseSubscriptions)
            {
                if (s.MID == MID)
                {
                    handlersToRemove.Add(s);
                }
            }
            for (int i = 0; i < handlersToRemove.Count; i++)
            {
                _pulseSubscriptions.Remove(handlersToRemove[i]);
            }
        }
        #endregion

        #region InternalMethods
        private void Init()
        {
            _liveRequests = new List<LiveAppRequest>();
            _disposed = false;
            _observationSubscriptions = new List<ObservationSubscription<Observation>>();
            _binarySubscriptions = new List<ObservationSubscription<byte[]>>();
            _booleanSubscriptions = new List<ObservationSubscription<BooleanObservation>>();
            _doubleSubscriptions = new List<ObservationSubscription<DoubleObservation>>();
            _integerSubscriptions = new List<ObservationSubscription<IntegerObservation>>();
            _positionSubscriptions = new List<ObservationSubscription<PositionObservation>>();
            _stringSubscriptions = new List<ObservationSubscription<StringObservation>>();
            _statisticsSubscriptions = new List<ObservationSubscription<StatisticsObservation>>();
            _commandSubscriptions = new List<CommandSubscription<Command>>();
            _commandResponseSubscriptions = new List<CommandSubscription<CommandResponse>>();
            _pulseSubscriptions = new List<PulseSubscription>();
        }

        private void RemoveHandler<T>(List<ObservationSubscription<T>> table, string MID, int observationId)
        {
            List<ObservationSubscription<T>> handlersToRemove = new List<ObservationSubscription<T>>();
            foreach (ObservationSubscription<T> s in table)
            {
                if (s.MID == MID && s.ObservationId == observationId)
                {
                    handlersToRemove.Add(s);
                }
            }
            for (int i = 0; i < handlersToRemove.Count; i++)
            {
                table.Remove(handlersToRemove[i]);
            }
        }

        private bool DispatchObservation(string MID, int observationId, ReadOnlySpan<byte> body, DataType dataType)
        {
            string json = (dataType == DataType.Binary) ? null : Encoding.UTF8.GetString(body);

            int count = 0;
            // Handle base observation subscriptions
            IEnumerable<ObservationSubscription<Observation>> subscriptions = _observationSubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.ObservationId == observationId);
            if (subscriptions.Any())
            {
                foreach (ObservationSubscription<Observation> s in subscriptions) // Iterate outside of switch for readability
                {
                    switch (dataType)
                    {
                        case DataType.Boolean:
                            s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<BooleanObservation>(json));
                            count++;
                            break;
                        case DataType.Double:
                            s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<DoubleObservation>(json));
                            count++;
                            break;
                        case DataType.Integer:
                            s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<IntegerObservation>(json));
                            count++;
                            break;
                        case DataType.Position:
                            s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<PositionObservation>(json));
                            count++;
                            break;
                        case DataType.String:
                            s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<StringObservation>(json));
                            count++;
                            break;
                        case DataType.Statistics:
                            s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<StatisticsObservation>(json));
                            count++;
                            break;
                    }
                }
            }
            else  // Handle specific observation type subscriptions
            {
                switch (dataType)
                {
                    case DataType.Binary:
                        IEnumerable<ObservationSubscription<byte[]>> binSubscription = _binarySubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.ObservationId == observationId);
                        if (binSubscription.Any())
                        {
                            foreach (ObservationSubscription<byte[]> s in binSubscription)
                            {
                                s.ObservationHandler(MID, observationId, body.ToArray());
                                count++;                                
                            }
                        }
                        break;
                    case DataType.Boolean:
                        IEnumerable<ObservationSubscription<BooleanObservation>> boolSubscription = _booleanSubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.ObservationId == observationId);
                        if (boolSubscription.Any())
                        {
                            foreach (ObservationSubscription<BooleanObservation> s in boolSubscription)
                            {
                                s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<BooleanObservation>(json));
                                count++;                                
                            }
                        }
                        break;
                    case DataType.Double:
                        IEnumerable<ObservationSubscription<DoubleObservation>> dblSubscription = _doubleSubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.ObservationId == observationId);
                        if (dblSubscription.Any())
                        {
                            foreach (ObservationSubscription<DoubleObservation> s in dblSubscription)
                            {
                                s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<DoubleObservation>(json));
                                count++;                                
                            }
                        }
                        break;
                    case DataType.Integer:
                        IEnumerable<ObservationSubscription<IntegerObservation>> intSubscription = _integerSubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.ObservationId == observationId);
                        if (intSubscription.Any())
                        {
                            foreach (ObservationSubscription<IntegerObservation> s in intSubscription)
                            {
                                s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<IntegerObservation>(json));
                                count++;                                
                            }
                        }
                        break;
                    case DataType.Position:
                        IEnumerable<ObservationSubscription<PositionObservation>> posSubscription = _positionSubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.ObservationId == observationId);
                        if (posSubscription.Any())
                        {
                            foreach (ObservationSubscription<PositionObservation> s in posSubscription)
                            {
                                s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<PositionObservation>(json));
                                count++;                                
                            }
                        }
                        break;
                    case DataType.String:
                        IEnumerable<ObservationSubscription<StringObservation>> strSubscription = _stringSubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.ObservationId == observationId);
                        if (strSubscription.Any())
                        {
                            foreach (ObservationSubscription<StringObservation> s in strSubscription)
                            {
                                s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<StringObservation>(json));
                                count++;
                            }
                        }
                        break;
                    case DataType.Statistics:
                        IEnumerable<ObservationSubscription<StatisticsObservation>> statSubscription = _statisticsSubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.ObservationId == observationId);
                        if (statSubscription.Any())
                        {
                            foreach (ObservationSubscription<StatisticsObservation> s in statSubscription)
                            {
                                s.ObservationHandler(MID, observationId, JsonConvert.DeserializeObject<StatisticsObservation>(json));
                                count++;
                            }
                        }
                        break;
                }
            }
            return count > 0;
        }

        private bool DispatchCommand(string MID, int commandId, string json, DateTime timestamp)
        {
            IEnumerable<CommandSubscription<Command>> subscriptions = _commandSubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.CommandId == commandId);
            if (subscriptions.Any())
            {
                foreach (CommandSubscription<Command> s in subscriptions)
                {
                    s.CommandHandler(MID, JsonConvert.DeserializeObject<Command>(json));                    
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool DispatchCommandResponse(string MID, int commandId, string json, DateTime timestamp)
        {
            IEnumerable<CommandSubscription<CommandResponse>> subscriptions = _commandResponseSubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.CommandId == commandId);
            if (subscriptions.Any())
            {
                foreach (CommandSubscription<CommandResponse> s in subscriptions)
                {
                    s.CommandHandler(MID, JsonConvert.DeserializeObject<CommandResponse>(json));   
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool DispatchPulse(string MID, int pulseId, string json)
        {
            IEnumerable<PulseSubscription> subscriptions = _pulseSubscriptions.Where(s => (s.MID == MID || s.MID == null) && s.PulseId == pulseId);
            if (subscriptions.Any())
            {
                foreach (PulseSubscription s in subscriptions)
                {
                    s.PulseHandler(MID, pulseId, JsonConvert.DeserializeObject<Pulse>(json));                    
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        private IBasicProperties GetMessageProperties(byte deliveryMode)
        {
            lock (_modelLock)
            {
                IBasicProperties properties = _model.CreateBasicProperties();
                properties.ContentType = "application/json";
                properties.DeliveryMode = deliveryMode;
                return properties;
            }
        }

        private void AppendMetadata(IBasicProperties properties)
        {
            properties.Headers = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(this.Metadata.Application)) properties.Headers.Add("OriginApplication", this.Metadata.Application);
            if (this.ConnectionDetails != null && !string.IsNullOrEmpty(this.ConnectionDetails.Username)) properties.Headers.Add("OriginAccount", this.ConnectionDetails.Username);
            if (!string.IsNullOrEmpty(_localAddress)) properties.Headers.Add("OriginAddress", _localAddress);
            if (!string.IsNullOrEmpty(this.Metadata.Reference)) properties.Headers.Add("OriginReference", this.Metadata.Reference);
        }

        private IDictionary<string, object> GetMessageHeader(BasicDeliverEventArgs args)
        {
            if (args == null) return new Dictionary<string, object>();
            if (args.BasicProperties == null) return new Dictionary<string, object>();
            return args.BasicProperties.Headers;
        }

        private string GetLocalIPAddress()
        {
            if (NetworkInterface.GetIsNetworkAvailable())
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            return null;
        }

        private bool Open()
        {
            if (_liveConnectionDetails.UseSsl)
            {
                var ssl = new SslOption();
                ssl.Enabled = true;
                if (IgnoreSslCertificateErrors)
                {
                    ssl.AcceptablePolicyErrors = SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;
                }
                ssl.ServerName = _liveConnectionDetails.Server;
                _connectionFactory = new ConnectionFactory
                {
                    HostName = _liveConnectionDetails.Server,
                    VirtualHost = _liveConnectionDetails.VirtualHost,
                    UserName = _liveConnectionDetails.Username,
                    Password = _liveConnectionDetails.Password,
                    RequestedHeartbeat = new TimeSpan(0, 0, _heartbeatInterval),
                    Port = _liveConnectionDetails.Port,
                    Ssl = ssl
                };
            }
            else
            {
                _connectionFactory = new ConnectionFactory
                {
                    HostName = _liveConnectionDetails.Server,
                    VirtualHost = _liveConnectionDetails.VirtualHost,
                    UserName = _liveConnectionDetails.Username,
                    Password = _liveConnectionDetails.Password,
                    RequestedHeartbeat = new TimeSpan(0, 0, _heartbeatInterval),
                    Port = _liveConnectionDetails.Port
                };
            }

            try
            {
                _connection = _connectionFactory.CreateConnection();

                if (_connection != null && _connection.IsOpen)
                {
                    lock (_modelLock)
                    {
                        _model = _connection.CreateModel();
                        _model.BasicQos(0, (ushort)this.PrefetchCount, false);
                        _transactionOpen = false;
                        return _model != null && _model.IsOpen;
                    }
                }
            }
            catch (Exception e)
            {
                LastErrorMessage = e.Message;
            }
            return false;  // Failed to create connection
        }

        private void ConsumerReceived(object sender, BasicDeliverEventArgs args)
        {
            if (UseAutomaticCallbacks)
            {
                Dispatch(args.RoutingKey, GetMessageHeader(args), args.Body.Span, args.DeliveryTag);
            }
            else
            {
                _queue.Enqueue(args);
            }
        }

        private bool Dispatch(string routingKey, IDictionary<string, object> headers, ReadOnlySpan<byte> body, ulong deliveryTag)
        {
            bool dispatched = false;
            string MID = MessageRoutingKey.ParseMID(routingKey);

            if (MID != null && MID.Length > 0 && body != null && body.Length > 0)
            {
                if (MessageRoutingKey.IsDeviceObservation(routingKey))
                {
                    int observationId = MessageRoutingKey.ParseObservationId(routingKey);
                    if (observationId != 0)
                    {
                        // Evaluate if to dispatch as single observation callback.
                        if (_observationType.ContainsKey(observationId))
                        {
                            dispatched = DispatchObservation(MID, observationId, body, _observationType[observationId]);
                        }
                    }
                }
                else if (MessageRoutingKey.IsDeviceCommand(routingKey))
                {
                    int commandId = MessageRoutingKey.ParseCommandId(routingKey);
                    if (commandId != 0)
                    {
                        string json = Encoding.UTF8.GetString(body);
                        DateTime timestamp = MessageRoutingKey.ParseCommandTimestamp(routingKey);
                        dispatched = DispatchCommand(MID, commandId, json, timestamp);
                    }
                }
                else if (MessageRoutingKey.IsDeviceCommandResponse(routingKey))
                {
                    int commandId = MessageRoutingKey.ParseCommandId(routingKey);
                    if (commandId != 0)
                    {
                        string json = Encoding.UTF8.GetString(body);
                        DateTime timestamp = MessageRoutingKey.ParseCommandTimestamp(routingKey);
                        dispatched = DispatchCommandResponse(MID, commandId, json, timestamp);
                    }
                }
                else if (MessageRoutingKey.IsDevicePulse(routingKey))
                {
                    string json = Encoding.UTF8.GetString(body);
                    if (MessageRoutingKey.IsDevicePulse(routingKey))
                    {
                        dispatched = DispatchPulse(MID, 0, json);
                    }
                    else if (MessageRoutingKey.IsApplicationPulse(routingKey))
                    {
                        int pulseId = MessageRoutingKey.ParsePulseId(routingKey);
                        dispatched = DispatchPulse(MID, pulseId, json);
                    }
                }
            }
            if (!UseAutomaticAcknowledgement)
            {
                lock (_modelLock)
                {
                    if (dispatched)
                    {
                        _model.BasicAck(deliveryTag, false);
                    }
                    else
                    {
                        _model.BasicNack(deliveryTag, false, false);
                    }
                }
            }

            return dispatched;
        }

        private bool OpenConnection()
        {
            if (_liveConnectionDetails != null)
            {
                if (Open())
                {
                    // Enable automatic callback handler if specified.
                    lock (_modelLock)
                    {
                        _consumer = new EventingBasicConsumer(_model);
                        _consumer.Received += ConsumerReceived;
                        try
                        {
                            _consumerTag = _model.BasicConsume(_liveConnectionDetails.QueueName, UseAutomaticAcknowledgement, _consumer);
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    }
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        private int ActiveObservationHandlers(int observationId)
        {
            int handlerCount = 0;
            handlerCount += _observationSubscriptions.Count(s => observationId == s.ObservationId);
            handlerCount += _binarySubscriptions.Count(s => observationId == s.ObservationId);
            handlerCount += _booleanSubscriptions.Count(s => observationId == s.ObservationId);
            handlerCount += _doubleSubscriptions.Count(s => observationId == s.ObservationId);
            handlerCount += _integerSubscriptions.Count(s => observationId == s.ObservationId);
            handlerCount += _positionSubscriptions.Count(s => observationId == s.ObservationId);
            handlerCount += _stringSubscriptions.Count(s => observationId == s.ObservationId);

            return handlerCount;
        }
        #endregion
    }
}