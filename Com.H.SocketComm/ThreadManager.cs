using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Linq;

namespace Com.H.SocketComm
{
    internal class ThreadManager
    {
        private class LockKey
        {
            public object Key { get; set; }
            public int Count { get; set; }
        }
        private readonly static List<LockKey> locks = new List<LockKey>();
        private readonly static object lockObj = new object();

        /// <summary>
        /// Controls the concurrent / multi-threaded calls to a single Action 
        /// by queuing multi-threaded calls to it and
        /// bouncing off (not executing) extra calls that exceed its queueLength parameter
        /// </summary>
        /// <param name="action"></param>
        /// <param name="key">By default, this extension method could automatically tell 
        /// the unique signature of the Action to determine whether or not 
        /// another thread is currently executing it.
        /// However, when the Action has input variable that aren't final 
        /// (e.g. SomeAction(5,2) <= final values, SomeAction(x,y) <= non final), 
        /// then a unique key is needed to identify the Action signature,
        /// otherwise this extension method would always default to allowing unlimited multi-threaded calls to 
        /// execute the Action if it couldn't identify its unique signature. </param>
        /// <param name="queueLength">Maximum queue length, default is 1 (i.e. only one call is allowed, any extra concurrent calls are ignored)</param>
        public static void QueueCall(Action action, int queueLength = 1, object key = null)
        {
            //var localCount = Interlocked.Increment(ref lockCount);
            if (key == null) key = action;
            LockKey lockKey = null;
            lock (lockObj)
            {
                lockKey = locks.FirstOrDefault(x => x.Key.Equals(key));

                if (lockKey != null && lockKey.Count > queueLength)
                    return;

                if (lockKey == null)
                    lockKey = new LockKey() { Key = key, Count = 1 };
                else
                    lockKey.Count++;
            }

            action();
            lock (lockObj)
            {
                locks.Remove(lockKey);
            }
        }
        private static int lockCount = 0;

        /// <summary>
        /// Controls the concurrent / multi-threaded calls to a single Func 
        /// by queuing multi-threaded calls to it and
        /// bouncing off (not executing) extra calls that exceed its queue_length parameter
        /// </summary>
        /// <typeparam name="T">Result value of the the Func.</typeparam>
        /// <param name="func">The Func delegate to be called</param>
        /// <param name="defaultValue">Default value of T that gets returned in the event of a calls is bounced off the queue</param>
        /// <param name="key">By default, this extension method could automatically tell 
        /// the unique signature of the Func to determine whether or not 
        /// another thread is currently executing it.
        /// However, when the Func has input variable that aren't final 
        /// (e.g. SomeFunc(5,2) <= final values, SomeFunc(x,y) <= non final), 
        /// then a unique key is needed to identify the Func signature,
        /// as otherwise this extension method would always default to allowing unlimited multi-threaded calls to 
        /// execute the Func if it couldn't identify its unique signature. 
        /// </param>
        /// <param name="queueLength"></param>
        /// <returns></returns>
        public static T QueueCall<T>(Func<T> func, 
            T defaultValue = default,
            int queueLength = 1,
            object key = null
            )
        {
            var localCount = Interlocked.Increment(ref lockCount);
            if (key == null) key = func;
            LockKey lockKey = null;
            lock (lockObj)
            {
                lockKey = locks.FirstOrDefault(x => x.Key.Equals(key));

                if (lockKey != null && lockKey.Count > queueLength)
                {
                    return defaultValue;
                }
                if (lockKey == null)
                    lockKey = new LockKey() { Key = key, Count = 1 };
                else
                    lockKey.Count++;

            }
            T result = func();
            lock (lockObj)
            {
                locks.Remove(lockKey);
                return result;
            }

        }

    }
}
