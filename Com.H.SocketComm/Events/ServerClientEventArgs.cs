using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Com.H.SocketComm.Events
{
    /// <summary>
    /// Author: Hussein Al Bayati, 2019
    /// Server ClientConnected Event Args
    /// </summary>

    public class ServerClientEventArgs : EventArgs
    {
        public object Sender { get; set; }
        public ServerClient Client { get; set; }
    }
}
