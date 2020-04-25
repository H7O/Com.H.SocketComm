using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace Com.H.SocketComm
{
    /// <summary>
    /// Author: Hussein Al Bayati, 2019
    /// TCP Server handler class
    /// </summary>

    public class Server : IDisposable
    {
        #region public
        /// <summary>
        /// You can use this cancellation token source to hook it up to other business logic you might have 
        /// giving you universal control over threads cancellation across your solution
        /// </summary>
        public CancellationTokenSource Cts { get; private set; }
        /// <summary>
        /// List of all active (non-disposed) clients. 
        /// Clients that are disconnected and not referenced in your application gets removed automatically from this list.
        /// </summary>
        public ConcurrentDictionary<Guid, ServerClient> Clients { get; private set; } = new ConcurrentDictionary<Guid, ServerClient>();

        public Encoding Encoding { get; set; } = Encoding.UTF8;
        #endregion

        #region private
        private TcpListener L { get; set; }
        
        private bool IsRunning { get; set; }
        private readonly object lockObj = new object();
        private IPEndPoint Ep { get; set; }

        #endregion
        /// <summary>
        /// Instantiate the Server class.
        /// </summary>
        /// <param name="port">Port at which the server will listen for connections from any remote IP</param>
        public Server(int port)
        {
            this.Ep = new IPEndPoint(IPAddress.Any, port);
        }

        /// <summary>
        /// Instantiate the Server class
        /// </summary>
        /// <param name="ep">IP Endpoint information</param>
        public Server(IPEndPoint ep)
        {
            this.Ep = ep;
        }

        /// <summary>
        /// Start listening to connections.
        /// </summary>
        /// <param name="backLog">The maximum length of the pending connections queue</param>
        public async Task Start(int? backLog = null)
        {
            try
            {
                lock (this.lockObj)
                {
                    if (this.IsRunning) return;
                    this.IsRunning = true;
                    this.disposedValue = false;
                    this.L = new TcpListener(this.Ep);
                    if (this.Cts == null || this.Cts.IsCancellationRequested)
                        this.Cts = new CancellationTokenSource();
                    if (backLog == null)
                        this.L.Start();
                    else this.L.Start((int) backLog);
                }

            }
            catch
            {
                this.Dispose(true);
                throw;
            }

            try
            {
                while (!this.Cts.IsCancellationRequested)
                {
                    Socket client = null;
                    try
                    {
                        client = await Task.Run(() => this.L.AcceptSocketAsync(), this.Cts.Token).ConfigureAwait(false);
                    }
                    catch (System.Net.Sockets.SocketException)
                    {
                        continue;
                    }
                    
                    _ = Task.Run(() =>
                    {
                        this.ProcessClient(client);
                    }, this.Cts.Token).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException){}
            catch (TaskCanceledException){}
            catch (OperationCanceledException){}
            catch (ThreadAbortException){}
            catch { throw; }
            finally { this.Dispose(true); }
        }

        /// <summary>
        /// Stop listening to connections.
        /// </summary>
        public void Stop()
        {
            this.Dispose(true);
        }

        private void ProcessClient(Socket client)
        {
            ServerClient sClient = null;

            if (this.Cts.IsCancellationRequested || this.disposedValue) return;

            try
            {
                sClient = new ServerClient(client, this);
            }
            catch
            {
                return;
            }

            if (sClient == null) return;
            try
            {
                using (var reg = this.Cts.Token.Register(Thread.CurrentThread.Abort))
                    OnClientConnected(new Events.ServerClientEventArgs() { Sender = this, Client = sClient });
            }
            catch (ObjectDisposedException){}
            catch (TaskCanceledException){}
            catch (OperationCanceledException){}
            catch (ThreadAbortException){}
            catch (Exception) { throw; }
            finally
            {
                if (sClient!=null && !sClient.DataMonitoringActive)
                {
                    Try(() => sClient.Disconnect());
                    sClient = null;
                }
            }

        }


        #region On Connected

        public delegate void ClientConnectedEventHandler(object sender, Events.ServerClientEventArgs e);

        /// <summary>
        /// Gets triggered whenever a client connects.
        /// </summary>
        public event ClientConnectedEventHandler ClientConnected;
        protected virtual void OnClientConnected(Events.ServerClientEventArgs e)
        {
            if (e == null) return;
            ClientConnected?.Invoke(e.Sender, e);
        }

        #endregion


        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch { }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    lock (this.lockObj)
                    {
                        this.IsRunning = false;
                        Try(() => this.L.Stop());
                        this.L = null;
                        Try(() => this.Cts.Cancel());
                    }
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~Server() {
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
