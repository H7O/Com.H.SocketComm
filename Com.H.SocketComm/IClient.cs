using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Com.H.SocketComm
{
    public interface IClient
    {
        Socket UnderlyingSocket { get; }
        NetworkStream Stream { get; }
        StreamWriter Writer { get; }
        StreamReader Reader { get; }
        CancellationTokenSource Cts { get; }
        int? IOReadTimeout { get; set; }
        /// <summary>
        /// Limits the maximum read legnth when calling ReadText() method.
        /// </summary>
        ulong? MaxReadTextLength { get; set; }
        /// <summary>
        /// Enables / disables read conformation messages when using WriteText() method
        /// </summary>
        bool DisableTextReadReceipts { get; set; }
        void WriteLineRaw(string data);
        /// <summary>
        /// Writes a string down the stream as is without any handshake nor receipt confirmation.
        /// </summary>
        /// <param name="data"></param>
        void WriteRaw(string data);
        /// <summary>
        /// Managed write operation, sends an 8 bytes lengh header followed by the data, then waits for
        /// client receipt acknowledgement.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="encoding"></param>
        /// <param name="bigEndian"></param>
        void Write(string data, Encoding encoding = null, bool bigEndian = false);

        string ReadLineRaw();
        /// <summary>
        /// Reads a string from the stream as is without any handshake nor receipt confirmation.
        /// </summary>
        /// <param name="length">Length of the expected stream. If lengh is greater than what's
        /// available on the stream, the operation will block until sufficient stream content is
        /// available before returning a string</param>
        /// <returns></returns>
        string ReadRaw(int length);
        /// <summary>
        /// Managed read operation, expects data to arrive that starts with an 8 bytes lengh header
        /// followed by the data, sends a confirmation message back to the sender, and then returns
        /// the received string back to the caller.
        /// </summary>
        /// <param name="encoding"></param>
        /// <param name="bigEndian"></param>
        /// <returns></returns>
        string Read(Encoding encoding = null, bool bigEndian = false);
        void Disconnect();
    }
}
