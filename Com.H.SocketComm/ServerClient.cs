using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Com.H.SocketComm
{
    /// <summary>
    /// Author: Hussein Al Bayati, 2019
    /// Server remote TCP client handler class
    /// </summary>
    public class ServerClient : IDisposable, IClient
    {
        #region properties
        #region public

        public Guid Id { get; private set; } = Guid.NewGuid();
        public Socket UnderlyingSocket { get; private set; }
        public NetworkStream Stream { get; private set; }
        public StreamWriter Writer { get; private set; }
        public StreamReader Reader { get; private set; }
        public int? HeartbeatInterval { get; set; }
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
        public CancellationTokenSource Cts { get; private set; }
        /// <summary>
        /// Maximum waiting time (in milisec) waiting for IO read operation from a stream.
        /// </summary>
        public int? IOReadTimeout { get; set; }
        public ulong? MaxReadTextLength { get; set; }
         public bool DisableTextReadReceipts { get; set; }
        /// <summary>
        /// Maximum allowed quiet time (in milisec) between incoming data transmissions from a remote host stream.
        /// I.e. if no incoming data is detected for n miliseconds, 
        /// the client would consider the connection to the remote host is severed, 
        /// and reacts accordingly by disconnecting and retry to reconnect (if retry option is enabled via RetryInterval property).
        /// </summary>
        public int? NoDataActivityTimeout { get; set; }
        public Server Server { get; private set; }
        #endregion

        #region private
        private CancellationTokenSource DataMonitoringCts { get; set; }
        private CancellationTokenSource HeartbeatCts { get; set; }
        private readonly object lockObj = new object();
        private readonly object writingLock = new object();
        private readonly object readingLock = new object();
        private byte[] buffer = Array.Empty<byte>();
        internal bool DataMonitoringActive { get; set; }
        #endregion
        #endregion


        #region constructor
        internal ServerClient(Socket c, Server server)
        {
            try
            {
                lock (this.lockObj)
                {
                    this.IsConnected = true;
                    this.UnderlyingSocket = c;
                    this.Server = server;
                    this.Stream = new NetworkStream(this.UnderlyingSocket);
                    this.Writer = new StreamWriter(this.Stream, this.Encoding);
                    this.Reader = new StreamReader(this.Stream, this.Encoding);
                    if (this.IOReadTimeout!=null)
                        this.UnderlyingSocket.ReceiveTimeout 
                            = this.Stream.ReadTimeout 
                            = (int) this.IOReadTimeout;
                    this.Cts = CancellationTokenSource.CreateLinkedTokenSource(this.Server.Cts.Token);
                    this.DataMonitoringCts = CancellationTokenSource.CreateLinkedTokenSource(this.Cts.Token);
                    this.HeartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(this.Cts.Token);

                    if (!this.Server.Clients.TryAdd(this.Id, this)) 
                        throw new Exception("Unable to add ServerClient object to Server.Clients dictionary");
                    Task.Run(() => ThreadManager.QueueCall(this.MonitorData, 2), 
                        this.DataMonitoringCts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                this.Dispose(true);
                throw;
            }
        }
        #endregion


        #region connection operations
        public void Disconnect()
        {
            this.Dispose(true);
        }

        #endregion

        #region data monitoring
        
        private void MonitorData()
        {
            try
            {
                // Hussein: check if data monitoring active flag is needed
                this.DataMonitoringActive = true;
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
                            var t = this.Stream?.ReadAsync(buffer, 0, 0, this.Cts.Token)
                                .CancellableWait((int)this.NoDataActivityTimeout, this.DataMonitoringCts.Token
                                , () => throw new SocketException(10060) // 10060 : Connection timed out
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
                            this.OnDisconnectedUnexpectedly(new Events.ServerClientEventArgs()
                            {
                                Sender = this,
                                Client = this
                            }),
                            this.DataMonitoringCts.Token);
                        // Hussein: this might need to be true
                        this.Dispose(false);
                        return;
                    }

                    #endregion

                    #region when data is available
                    if (this.DataMonitoringCts.IsCancellationRequested || this.dataAvailable is null) return;

                    CancellableExt.CancellableRun(() =>
                        this.OnDataAvailable(
                            new Events.ServerClientEventArgs() { Sender = this, Client = this }),
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
            finally
            {
                this.DataMonitoringActive = false;
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
                        this.OnHeartbeat(new Events.ServerClientEventArgs() { Sender = this, Client = this }),
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

        #region IO Operations
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
                this.Dispose(false);
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
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected
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
                this.Dispose(false);
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
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected
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
                this.Dispose(false);
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
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected
                lock (this.writingLock)
                {
                    this.Writer.WriteLine(content);
                    this.Writer.Flush();
                }
            }
            catch { this.Dispose(false); throw; }
        }

        public void WriteRaw(string content)
        {
            try
            {
                if (!this.IsConnected
                    || this.UnderlyingSocket == null 
                    || !this.UnderlyingSocket.Connected 
                    || this.Writer == null 
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected
                lock (this.writingLock)
                {
                    this.Writer.Write(content);
                    this.Writer.Flush();
                }
            }
            catch { this.Dispose(false); throw; }
        }


        /// <summary>
        /// Write a string that is preceded by it's length in bytes. The length in bytes is represented by a 64 bit integer.
        /// </summary>
        /// <param name="data">Test to be written down the stream</param>
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
                    || this.disposedValue) throw new SocketException(10057); // Socket is not connected
                lock (this.writingLock)
                {
                    if (encoding == null) encoding = Encoding.UTF8;
                    var content = encoding.GetBytes(data);
                    UInt64 length = (UInt64) content.LongLength;

                    this.Writer.BaseStream.Write(
                        BitConverter.IsLittleEndian != bigEndian ?
                        BitConverter.GetBytes(length) :
                        BitConverter.GetBytes(length).Reverse().ToArray(),
                        0, 8);

                    this.Writer.BaseStream.Flush();
                    for (int count = length > int.MaxValue ? int.MaxValue : (int)length;
                        length > 0;
                        count = (length -= (ulong) count) > int.MaxValue ? int.MaxValue : (int)length)
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
            catch { this.Dispose(false); throw; }
        }


        #endregion

        #region events

        #region On Data Available

        public delegate void DataAvailableEventHandler(object sender, Events.ServerClientEventArgs e);

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
        protected virtual void OnDataAvailable(Events.ServerClientEventArgs e)
        {
            if (e == null) return;
            dataAvailable?.Invoke(e.Sender, e);
        }

        #endregion        


        #region On Disconnected Unexpectedly

        public delegate void DisconnectedUnexpectedlyEventHandler(object sender, Events.ServerClientEventArgs e);

        /// <summary>
        /// Triggered whenever a remote connection gets disconnected unexpectedly (due to network connection disruption not when calling Disconnect() method).
        /// </summary>
        public event DisconnectedUnexpectedlyEventHandler DisconnectedUnexpectedly;
        protected virtual void OnDisconnectedUnexpectedly(Events.ServerClientEventArgs e)
        {
            if (e == null) return;
            DisconnectedUnexpectedly?.Invoke(e.Sender, e);
        }

        #endregion

        #region On Heartbeat

        public delegate void HeartbeatEventHandler(object sender, Events.ServerClientEventArgs e);

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
        protected virtual void OnHeartbeat(Events.ServerClientEventArgs e)
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
            if (disposing)
            {
                Try(() => this.Cts.Cancel());
                Try(() => this.Server.Clients.TryRemove(this.Id, out _));
            }

            if (!disposedValue)
            {
                if (disposing)
                {
                    lock (this.lockObj)
                    {
                        this.IsConnected = false;
                        Try(() => this.Writer.Flush());
                        Try(() => this.Writer.Close());
                        Try(() => this.Writer.Dispose());
                        this.Writer = null;

                        Try(() => this.Reader.Close());
                        Try(() => this.Reader.Dispose());
                        this.Reader = null;

                        Try(() => this.Stream.Close());
                        Try(() => this.Stream.Dispose());
                        this.Stream = null;

                        Try(() => this.UnderlyingSocket.Close());
                        Try(() => this.UnderlyingSocket.Dispose());
                        this.UnderlyingSocket = null;
                    }
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
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
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion

    }
}
