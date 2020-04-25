using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Com.H.SocketComm.Json
{
    /// <summary>
    /// Author: Hussein Al Bayati, 2019
    /// Extensions for SocketComm to further extend SocketComm capabilities.
    /// </summary>
    public static class Extensions
    {
        /// <summary>
        /// Serializes then writes an object in JSON format down the stream
        /// </summary>
        /// <typeparam name="T">Type of the object to be send</typeparam>
        /// <param name="client">The SocketCom API client object</param>
        /// <param name="data">The data object to be send</param>
        public static void WriteJson<T>(this IClient client, T data) 
            => client.Write(JsonSerializer.Serialize<T>(data));

        /// <summary>
        /// Retrieves then deserializes a JSON object up from the stream
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="client"></param>
        /// <returns>Deserialized object</returns>
        public static T ReadJson<T>(this IClient client) 
            => JsonSerializer.Deserialize<T>(client.Read());

    }
}
