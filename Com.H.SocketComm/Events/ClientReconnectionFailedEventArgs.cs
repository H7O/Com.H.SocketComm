using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Com.H.SocketComm.Events
{
    public class ClientReconnectionFailedEventArgs : EventArgs
    {
        public Exception Exception { get; set; }
        public object Sender { get; set; }
        public Client Client { get; set; }
    }
}
