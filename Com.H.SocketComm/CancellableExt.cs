using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Com.H.SocketComm
{
    internal static class CancellableExt
    {
        public static Task<T> CancellableWait<T>(this Task<T> task, 
            int? timeout = null, 
            CancellationToken? token = null,
            Action actionOnTimeout = null
            )
        {
            timeout = timeout ?? -1;
            var delayTask = token==null? 
                Task.Delay((int)timeout): 
                Task.Delay((int)timeout, (CancellationToken) token);
            var result = Task.WhenAny(task, delayTask).Result;

            if (actionOnTimeout!=null 
                && result == delayTask 
                && delayTask.IsCompleted) actionOnTimeout();

            return task;
        }

        public static void CancellableRun(Action action, CancellationToken token)
        {
            try
            {
                Task.Run(() =>
                {
                    using (var reg = token.Register(Thread.CurrentThread.Abort))
                        try
                        {
                            action();
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
                            throw;
                        }

                }, token).Wait(token);
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
                throw;
            }
        }
    }
}
