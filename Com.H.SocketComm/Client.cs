using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using System.Threading.Tasks;

namespace Com.H.SocketComm
{
    /// <summary>
    /// Author: Hussein Al Bayati, 2019
    /// TCP client handler class
    /// </summary>
    public class Client : IDisposable, IClient
    {
        #region properties
        #region public
        public Socket UnderlyingSocket { get; private set; }
        public NetworkStream Stream { get; set; }
        public StreamWriter Writer { get; private set; }
        public StreamReader Reader { get; private set; }
        public int? HeartbeatInterval { get; set; }
        public SocketType SocketType { get; set; } = SocketType.Stream;
        public ProtocolType ProtocolType { get; set; } = ProtocolType.Tcp;
        private Encoding encoding;
        public Encoding Encoding
        {
            get => encoding ?? (encoding = Encoding.UTF8);
            set
            {
                this.encoding = value;
                this.Writer = new StreamWriter(this.Stream, encoding);
                this.Reader = new StreamReader(this.Stream, encoding);
            }
        }

        public bool IsConnected { get; private set; }
        public ulong? MaxReadTextLength { get; set; }
        public bool DisableTextReadReceipts { get; set; }
        public CancellationTokenSource Cts { get; private set; }

        /// <summary>
        /// Retry reconnection interval in miliseconds in the event of a unexpected network disconnection.
        /// </summary>
        public int? RetryInterval { get; set; }
        /// <summary>
        /// Maximum waiting time (in milisec) waiting for IO read operation from a stream.
        /// </summary>
        public int? IOReadTimeout { get; set; }
        /// <summary>
        /// Maximum allowed quiet time (in milisec) between incoming data transmissions from a remote host stream.
        /// I.e. if no incoming data is detected for n miliseconds, 
        /// the client would consider the connection to the remote host is severed, 
        /// and reacts accordingly by disconnecting and retry to reconnect (if retry option is enabled via RetryInterval property).
        /// </summary>
        public int? NoDataActivityTimeout { get; set; }
        #endregion
        #region private
        private CancellationTokenSource DataMonitoringCts { get; set; }
        private CancellationTokenSource HeartbeatCts { get; set; }
        private IPEndPoint Ep { get; set; }
        private bool OnConnectedTriggered { get; set; }
        private readonly object lockObj = new object();
        private readonly object writingLock = new object();
        private readonly object readingLock = new object();
        private byte[] buffer = Array.Empty<byte>();

        #endregion


        #endregion


        #region constructors
        public Client(IPEndPoint ipEndPoint)
        {
            this.Ep = ipEndPoint;
        }
        public Client(
            string hostOrIp,
            int port)
        {
            if (!IPAddress.TryParse(hostOrIp, out _))
            {
                var ip = Dns.GetHostEntry(hostOrIp).AddressList.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?? Dns.GetHostEntry(hostOrIp).AddressList.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
                if (ip == null) throw new Exception("Unable to resolve IPv4 or IPv6 for host '" + hostOrIp + "'.");
                this.Ep = new IPEndPoint(ip, port);
            }
            else this.Ep = new IPEndPoint(IPAddress.Parse(hostOrIp), port);
        }


        #endregion

        #region connection operations
        public bool Connect()
        {
            try
            {
                this.OnConnectedTriggered = false;
                lock (this.lockObj)
                {
                    if (!this.EstablishConnection()) return false;
                }

            }
            catch (SocketException)
            {
                if (this.RetryInterval != null)
                {
                    Task.Run(this.Reconnect, this.Cts.Token).ConfigureAwait(false);
                    return false;
                }
                throw;
            }
            this.OnConnectedTriggered = true;
            CancellableExt.CancellableRun(() => 
            {
                OnConnected(new Events.ClientEventArgs()
                {
                    Client = this,
                    Sender = this
                });
                
            }, this.Cts.Token);
            Task.Run(() => ThreadManager.QueueCall(this.MonitorData, 2), this.DataMonitoringCts.Token).ConfigureAwait(false);
            return true;
        }

        private bool EstablishConnection()
        {
            try
            {
                if (this.IsConnected) return true;
                this.disposedValue = false;

                this.UnderlyingSocket = new Socket(this.SocketType, this.ProtocolType);

                if (this.Cts == null || this.Cts.IsCancellationRequested)
                {
                    this.Cts = new CancellationTokenSource();
                    this.DataMonitoringCts = CancellationTokenSource.CreateLinkedTokenSource(this.Cts.Token);
                    this.HeartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(this.Cts.Token);
                }

                this.UnderlyingSocket.Connect(this.Ep);

                this.IsConnected = true;
                if (this.IOReadTimeout != null)
                    UnderlyingSocket.ReceiveTimeout = (int)this.IOReadTimeout;

                this.Stream = new NetworkStream(this.UnderlyingSocket);

                if (this.IOReadTimeout != null)
                    this.Stream.ReadTimeout = (int)this.IOReadTimeout;

                this.Writer = new StreamWriter(this.Stream, this.Encoding);
                this.Reader = new StreamReader(this.Stream, this.Encoding);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (ThreadAbortException)
            {
                return false;
            }
            catch
            {
                throw;
            }
            return true;
        }



        public void Disconnect()
        {
            this.Dispose(true);
        }

        private void Reconnect()
        {
            try
            {
                if (this.IsConnected) return;
                while (!this.Cts.IsCancellationRequested && this.RetryInterval != null)
                {
                    this.Dispose(false);

                    try
                    {
                        Task.Delay((int)this.RetryInterval, this.Cts.Token).Wait(this.Cts.Token);
                    }
                    catch
                    {
                        return;
                    }

                    try
                    {
                        lock (this.lockObj)
                        {
                            if (this.Cts != null && this.Cts.IsCancellationRequested) return;
                            if (!this.EstablishConnection())
                            {
                                continue;
                            }
                        }
                    }
                    catch (SocketException ex)
                    {
                        CancellableExt.CancellableRun(() =>
                        {
                            this.OnReconnectionFailure(
                                new Events.ClientReconnectionFailedEventArgs()
                                {
                                    Client = this,
                                    Exception = ex,
                                    Sender = this
                                });
                        },this.Cts.Token);
                        continue;
                    }


                    if (this.OnConnectedTriggered)
                    {
                        CancellableExt.CancellableRun(() =>
                        {
                            this.OnReconnected(new Events.ClientEventArgs()
                            {
                                Sender = this,
                                Client = this
                            });
                        }, this.Cts.Token);
                    }
                    else
                    {
                        this.OnConnectedTriggered = true;
                        CancellableExt.CancellableRun(() =>
                        {
                            this.OnConnected(new Events.ClientEventArgs()
                            {
                                Sender = this,
                                Client = this
                            });
                            
                        }, this.Cts.Token);
                    }
                    Task.Run(() =>
                        ThreadManager.QueueCall(this.MonitorData, 2)
                    , this.DataMonitoringCts.Token).ConfigureAwait(false);
                    return;
                }
            }
            catch { throw; }

        }


        #endregion


        #region data monitoring
        private void MonitorData()
        {
            try
            {

                if (this.DataMonitoringCts.IsCancellationRequested
                    ||
                    this.dataAvailable == null
                    ||
                    !this.IsConnected
                    )
                {
                    return;
                }

                while (!this.DataMonitoringCts.IsCancellationRequested && this.dataAvailable != null)
                {
                    #region awaiting data
                    bool unexpectedDisconnection = false;
                    try
                    {
                        if (this.NoDataActivityTimeout != null)
                        {
                            var t = this.Stream?.ReadAsync(buffer, 0, 0, this.DataMonitoringCts.Token)
                                .CancellableWait((int)this.NoDataActivityTimeout, this.DataMonitoringCts.Token
                                ,()=> throw new SocketException(10060) // 10060 : Connection timed out
                                );
                            if (!t.IsCompleted) throw new SocketException(10053); // 10053: Software caused connection abort.
                        }
                        else
                            this.Stream?.ReadAsync(buffer, 0, 0, this.DataMonitoringCts.Token)
                                .Wait(this.DataMonitoringCts.Token);
                    }
                    catch (SocketException)
                    {
                        unexpectedDisconnection = true;
                    }
                    catch (IOException)
                    {
                        unexpectedDisconnection = true;
                    }

                    #endregion

                    #region unexpected disconnection while waiting for data

                    if (
                        unexpectedDisconnection
                        ||
                        this.UnderlyingSocket == null
                        ||
                        this.UnderlyingSocket?.Available < 1
                        )
                    {
                        this.IsConnected = false;
                        CancellableExt.CancellableRun(() =>
                        {
                            this.OnDisconnectedUnexpectedly(new Events.ClientEventArgs()
                            {
                                Sender = this,
                                Client = this
                            });
                            
                        },
                            this.DataMonitoringCts.Token);

                        if (this.RetryInterval == null)
                        {
                            this.Dispose(false);
                            return;
                        }

                        Task.Run(() => ThreadManager.QueueCall(this.Reconnect, 2),
                            this.Cts.Token);
                        return;
                    }

                    #endregion

                    #region when data is available
                    if (this.DataMonitoringCts.IsCancellationRequested || this.dataAvailable is null) return;

                    CancellableExt.CancellableRun(() =>
                    {
                        this.OnDataAvailable(
                            new Events.ClientEventArgs() { Sender = this, Client = this });
                    },
                            this.DataMonitoringCts.Token);
                    #endregion

                }

            }
            catch (ObjectDisposedException)
            {
            }
            catch (TaskCanceledException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (ThreadAbortException)
            {
            }
            catch
            {
                this.Dispose(false);
                throw;
            }

        }
        #endregion

        #region generate heartbeats
        private void GenerateHeartBeats()
        {
            try
            {
                if (this.HeartbeatCts.IsCancellationRequested
                    ||
                    this.heartbeat == null
                    ||
                    !this.IsConnected
                    ||
                    this.HeartbeatInterval == null
                    ||
                    this.HeartbeatInterval < 1
                    )
                {
                    return;
                }

                while (!this.HeartbeatCts.IsCancellationRequested && this.heartbeat != null)
                {
                    Task.Delay((int)this.HeartbeatInterval, this.HeartbeatCts.Token).Wait(this.HeartbeatCts.Token);
                    CancellableExt.CancellableRun(() =>
                        this.OnHeartbeat(new Events.ClientEventArgs() { Sender = this, Client = this }),
                        this.HeartbeatCts.Token);
                }

            }
            catch (ObjectDisposedException)
            {
            }
            catch (TaskCanceledException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (ThreadAbortException)
            {
            }
            catch
            {
                this.Dispose(false);
                throw;
            }


        }
        #endregion

        #region IO operations

        public string ReadLineRaw()
        {
            try
            {
                if (!this.IsConnected
                    || this.UnderlyingSocket == null
                    || !this.UnderlyingSocket.Connected
                    || this.Reader == null
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected
                lock (this.readingLock)
                {
                    var t = this.Reader.ReadLineAsync()
                        .CancellableWait(this.IOReadTimeout,
                        this.Cts.Token,
                        () =>
                            throw new SocketException(10060) //  10060: Connection timed out.
                        );
                    if (!t.IsCompleted)
                        throw new SocketException(10053); //  10053: Software caused connection abort.
                    return ((Task<string>)t).Result;
                }
            }
            catch
            {
                Try(()=>this.Stream.Close());
                if (!this.Cts.IsCancellationRequested)
                    Task.Run(() => ThreadManager.QueueCall(this.Reconnect, 2), this.Cts.Token).ConfigureAwait(false);
                else this.Dispose(false);
                throw;
            }
        }

        public string ReadRaw(int length)
        {
            try
            {
                if (!this.IsConnected
                    || this.UnderlyingSocket == null
                    || !this.UnderlyingSocket.Connected
                    || this.Reader == null
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected.
                lock (this.readingLock)
                {
                    char[] content = new char[length];
                    Task t = this.Reader.ReadAsync(content, 0, length)
                        .CancellableWait(this.IOReadTimeout,
                        this.Cts.Token,
                        () =>
                            throw new SocketException(10060) //  10060: Connection timed out.
                        );

                    if (!t.IsCompleted)
                        throw new SocketException(10053); //  10053: Software caused connection abort.
                    return new string(content);
                }
            }
            catch
            {
                Try(()=>this.Stream.Close());
                if (!this.Cts.IsCancellationRequested)
                    Task.Run(() => ThreadManager.QueueCall(this.Reconnect, 2), this.Cts.Token).ConfigureAwait(false);
                else this.Dispose(false);
                throw;
            }

        }
        /// <summary>
        /// Reads a block of text with its length defined in the first 8 bytes of the incoming stream content.
        /// </summary>
        /// <param name="encoding">Encoding of the incoming text</param>
        /// <param name="bigEndian">Bytes order of the 8 bytes text length</param>
        /// <returns></returns>
        public string Read(Encoding encoding = null, bool bigEndian = false)
        {
            try
            {
                if (!this.IsConnected
                    || this.UnderlyingSocket == null
                    || !this.UnderlyingSocket.Connected
                    || this.Reader == null
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected.
                lock (this.readingLock)
                {
                    byte[] lengthBytes = new byte[8];

                    Task t = this.Reader.BaseStream.ReadAsync(lengthBytes, 0, 8)
                        .CancellableWait(this.IOReadTimeout,
                        this.Cts.Token,
                        () =>
                            throw new SocketException(10060) //  10060: Connection timed out.
                        );


                    if (!t.IsCompleted)
                        throw new SocketException(10053); //  10053: Software caused connection abort.

                    var length = BitConverter.ToUInt64(
                        BitConverter.IsLittleEndian ^ !bigEndian ?
                        lengthBytes.Reverse().ToArray() :
                        lengthBytes.ToArray(), 0);

                    if (this.MaxReadTextLength < length) throw new SocketException(10040); // 10040: Message too long

                    byte[] content = new byte[length];
                    string result = "";
                    if (encoding == null) encoding = Encoding.UTF8;
                    for (int count = length > int.MaxValue ? int.MaxValue : (int)length;
                        length > 0;
                        count = (length -= (ulong)count) > int.MaxValue ? int.MaxValue : (int)length)
                    {
                        t = this.Reader.BaseStream.ReadAsync(content, 0, count)
                                            .CancellableWait(this.IOReadTimeout,
                                            this.Cts.Token,
                                            () =>
                                                throw new SocketException(10060) //  10060: Connection timed out.
                                            );
                        if (!t.IsCompleted)
                            throw new SocketException(10053); //  10053: Software caused connection abort.
                        result += encoding.GetString(content, 0, count);
                    }
                    if (!this.DisableTextReadReceipts)
                    {
                        this.WriteRaw("ok");
                    }
                    return result;
                }
            }
            catch
            {
                Try(() => this.Stream.Close());
                if (!this.Cts.IsCancellationRequested)
                    Task.Run(() => ThreadManager.QueueCall(this.Reconnect, 2), this.Cts.Token).ConfigureAwait(false);
                else this.Dispose(false);
                throw;
            }


        }

        public void WriteLineRaw(string content)
        {
            try
            {
                if (!this.IsConnected 
                    || this.UnderlyingSocket == null 
                    || !this.UnderlyingSocket.Connected 
                    || this.Writer == null 
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected.
                lock (this.writingLock)
                {
                    this.Writer.WriteLine(content);
                    this.Writer.Flush();
                }

            }
            catch
            {
                if (!this.Cts.IsCancellationRequested)
                    Task.Run(() => ThreadManager.QueueCall(this.Reconnect, 2), this.Cts.Token).ConfigureAwait(false);
                else this.Dispose(false);
                throw;
            }
        }

        public void WriteRaw(string content)
        {
            try
            {
                if (!this.IsConnected 
                    || this.UnderlyingSocket == null 
                    || !this.UnderlyingSocket.Connected 
                    || this.Writer == null 
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected.
                lock (this.writingLock)
                {
                    this.Writer.Write(content);
                    this.Writer.Flush();
                }
            }
            catch
            {
                if (!this.Cts.IsCancellationRequested)
                    Task.Run(() => ThreadManager.QueueCall(this.Reconnect, 2), this.Cts.Token).ConfigureAwait(false);
                else this.Dispose(false);
                throw;
            }
        }
        /// <summary>
        /// Writes a string that is preceded by it's length in bytes. The length in bytes is represented by a 64 bit integer (8 bytes).
        /// </summary>
        /// <param name="data">Text to be written down the stream</param>
        /// <param name="encoding">Optional, default (if left empty) is UTF8</param>
        /// <param name="bigEndian">Optional, the representation of the 64bit string length byte order when send down the stream. Default is little endian (i.e. bigEndian = false)</param>
        public void Write(string data, Encoding encoding = null, bool bigEndian = false)
        {
            try
            {
                if (!this.IsConnected
                    || this.UnderlyingSocket == null
                    || !this.UnderlyingSocket.Connected
                    || this.Writer == null
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected.
                lock (this.writingLock)
                {
                    if (encoding == null) encoding = Encoding.UTF8;
                    var content = encoding.GetBytes(data);
                    UInt64 length = (UInt64)content.LongLength;

                    this.Writer.BaseStream.Write(
                        BitConverter.IsLittleEndian != bigEndian ?
                        BitConverter.GetBytes(length) :
                        BitConverter.GetBytes(length).Reverse().ToArray(),
                        0, 8);

                    this.Writer.BaseStream.Flush();
                    for (int count = length > int.MaxValue ? int.MaxValue : (int)length;
                        length > 0;
                        count = (length -= (ulong)count) > int.MaxValue ? int.MaxValue : (int)length)
                    {
                        this.Writer.BaseStream.Write(content, 0, count);
                        this.Writer.BaseStream.Flush();
                    }

                    if (!this.DisableTextReadReceipts)
                    {
                        var response = this.ReadRaw(2);
                        if (string.IsNullOrEmpty(response) 
                            || !response.Equals("ok", StringComparison.InvariantCulture))
                            throw new SocketException(10107); // 10107: System call failure.
                    }

                }
            }
            catch
            {
                if (!this.Cts.IsCancellationRequested)
                    Task.Run(() => ThreadManager.QueueCall(this.Reconnect, 2), this.Cts.Token).ConfigureAwait(false);
                else this.Dispose(false);
                throw;
            }

        }
        #endregion

        #region events
        #region On Data Available

        public delegate void DataAvailableEventHandler(object sender, Events.ClientEventArgs e);

        private event DataAvailableEventHandler dataAvailable;

        /// <summary>
        /// Gets triggered whenever data is available on input stream.
        /// </summary>
        public event DataAvailableEventHandler DataAvailable
        {
            add
            {
                dataAvailable += value;
                if (this.IsConnected
                    && this.DataMonitoringCts != null
                    && !this.DataMonitoringCts.IsCancellationRequested)
                {
                    if ((
                        this.DataMonitoringCts == null
                        ||
                        this.DataMonitoringCts.IsCancellationRequested
                        )
                        &&
                        !this.Cts.IsCancellationRequested
                        )
                        this.DataMonitoringCts = CancellationTokenSource.CreateLinkedTokenSource(this.Cts.Token);
                    Task.Run(() =>
                        ThreadManager.QueueCall(this.MonitorData),
                        this.DataMonitoringCts.Token).ConfigureAwait(false);
                }
            }
            remove
            {
                dataAvailable -= value;
                if (dataAvailable is null) this.DataMonitoringCts.Cancel();
            }
        }
        protected virtual void OnDataAvailable(Events.ClientEventArgs e)
        {
            if (e == null) return;
            dataAvailable?.Invoke(e.Sender, e);
        }

        #endregion

        #region On Connected

        public delegate void ConnectedEventHandler(object sender, Events.ClientEventArgs e);

        /// <summary>
        /// Gets triggered when a remote connection is established for the first time, and when re-established after an unexpected disruption to the remote connection.
        /// </summary>
        public event ConnectedEventHandler Connected;
        protected virtual void OnConnected(Events.ClientEventArgs e)
        {
            if (e == null) return;
            Connected?.Invoke(e.Sender, e);
        }

        #endregion

        #region On Reconnected

        public delegate void ReconnectedEventHandler(object sender, Events.ClientEventArgs e);

        /// <summary>
        /// Triggered whenever a remote connection gets re-established after
        /// an unexpected disruption to the remote connection.
        /// </summary>
        public event ReconnectedEventHandler Reconnected;
        protected virtual void OnReconnected(Events.ClientEventArgs e)
        {
            if (e==null) return;
            Reconnected?.Invoke(e.Sender, e);
        }

        #endregion

        #region On ReconnectionFailure

        public delegate void ReconnectionFailureEventHandler(object sender, Events.ClientReconnectionFailedEventArgs e);

        /// <summary>
        /// Triggered whenever a remote reconnection attempt is failed.
        /// </summary>
        public event ReconnectionFailureEventHandler ReconnectionFailure;
        protected virtual void OnReconnectionFailure(Events.ClientReconnectionFailedEventArgs e)
        {
            if (e == null) return;
            ReconnectionFailure?.Invoke(e.Sender, e);
        }

        #endregion

        #region On Disconnected Unexpectedly

        public delegate void DisconnectedUnexpectedlyEventHandler(object sender, Events.ClientEventArgs e);

        /// <summary>
        /// Triggered whenever a remote connection gets disconnected unexpectedly (due to network connection disruption not when calling Disconnect() method).
        /// </summary>
        public event DisconnectedUnexpectedlyEventHandler DisconnectedUnexpectedly;
        protected virtual void OnDisconnectedUnexpectedly(Events.ClientEventArgs e)
        {
            if (e == null) return;
            DisconnectedUnexpectedly?.Invoke(e.Sender, e);
        }

        #endregion

        #region On Heartbeat

        public delegate void HeartbeatEventHandler(object sender, Events.ClientEventArgs e);

        private event HeartbeatEventHandler heartbeat;

        /// <summary>
        /// Gets triggered whenever heartbeat timer is up.
        /// </summary>
        public event HeartbeatEventHandler Heartbeat
        {
            add
            {
                heartbeat += value;
                if (this.IsConnected
                    && this.HeartbeatCts != null
                    && !this.HeartbeatCts.IsCancellationRequested)
                {
                    if ((
                        this.HeartbeatCts == null
                        ||
                        this.HeartbeatCts.IsCancellationRequested
                        )
                        &&
                        !this.Cts.IsCancellationRequested
                        )
                        this.HeartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(this.Cts.Token);

                    Task.Run(() =>
                        ThreadManager.QueueCall(this.GenerateHeartBeats),
                        this.HeartbeatCts.Token);
                }
            }
            remove
            {
                heartbeat -= value;
                if (heartbeat is null) this.DataMonitoringCts.Cancel();
            }
        }
        protected virtual void OnHeartbeat(Events.ClientEventArgs e)
        {
            if (e == null) return;
            heartbeat?.Invoke(e.Sender, e);
        }

        #endregion        


        #endregion



        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        private void Try(Action action)
        {
            try
            {
                action();
            }
            catch { }
        }
        protected virtual void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Try(() => this.Cts.Cancel());
                }
                if (!disposedValue)
                {
                    lock (this.lockObj)
                    {
                        Try(() => this.Writer.Flush());
                        Try(() => this.Writer.Close());
                        Try(() => this.Writer.Dispose());
                        this.Writer = null;
                        Try(() => this.Reader.Close());
                        Try(() => this.Reader.Close());
                        Try(() => this.Reader.Dispose());
                        this.Reader = null;
                        Try(() => this.Stream.Close());
                        Try(() => this.Stream.Dispose());
                        this.Stream = null;
                        Try(() => this.UnderlyingSocket.Close());
                        Try(() => this.UnderlyingSocket.Dispose());
                        this.UnderlyingSocket = null;
                        this.IsConnected = false;

                    }

                    // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                    // TODO: set large fields to null.

                    disposedValue = true;

                }
            }
            catch {}
        }


        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~Client() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        void IDisposable.Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            //ThreadManager.IgnoreOtherCallsWhileExecuting(() => this.Dispose(true));
            this.Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion

    }
}
