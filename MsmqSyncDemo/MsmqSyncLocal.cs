using System;
using System.Collections.Concurrent;
using System.Messaging;
using System.Threading;
using System.Threading.Tasks;

namespace MsmqSyncDemo
{
    public sealed class MsmqSyncLocal : IMsmqProcessor
    {
        private readonly CancellationTokenSource m_stopToken = new CancellationTokenSource();
        private readonly TimeSpan m_timeout;
        private readonly MessageQueue m_inputQueue;
        private readonly MessageQueue m_outputQueue;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> m_items = new ConcurrentDictionary<string, TaskCompletionSource<string>>();
        private readonly SemaphoreSlim m_semaphore;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> m_bag = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        private readonly Func<string, Exception> m_exceptionHandler;

        private static readonly ActiveXMessageFormatter ActiveXFormatter = new ActiveXMessageFormatter();

        public MsmqSyncLocal(string inputQueue, string outputQueue, TimeSpan timeout, Func<string, Exception> exceptionHandler, int bufferSize = 1024)
        {
            m_exceptionHandler = exceptionHandler;

            m_inputQueue = new MessageQueue(string.Format(@".\private$\{0}", inputQueue), QueueAccessMode.Send)
            {
                Formatter = ActiveXFormatter
            };

            m_outputQueue = new MessageQueue(string.Format(@".\private$\{0}", outputQueue), QueueAccessMode.Receive)
            {
                Formatter = ActiveXFormatter,
                MessageReadPropertyFilter = {Id = true, CorrelationId = true, Body = true, Label = true},
                DenySharedReceive = true
            };

            m_timeout = timeout;
            m_semaphore = new SemaphoreSlim(bufferSize);

            LogErrors(ReceiveMessagesAsync());
        }

        public async Task<string> ProcessAsync(string data, CancellationToken ct)
        {
            await m_semaphore.WaitAsync(ct);

            var tcs = new TaskCompletionSource<string>();
            var message = new Message(data, ActiveXFormatter);

            m_inputQueue.Send(message);

            var id = message.Id;

            m_items.TryAdd(id, tcs);

            var tcsForBag = new TaskCompletionSource<bool>();
            if (!m_bag.TryAdd(id, tcsForBag))
                m_bag[id].TrySetResult(true);

            var task = await Task.WhenAny(Task.Delay(m_timeout, ct), tcs.Task);

            if (task != tcs.Task)
            {
                if (m_items.TryRemove(id, out tcs))
                {
                    m_semaphore.Release();
                    if (ct.IsCancellationRequested)
                        tcs.TrySetCanceled();
                    else
                        tcs.TrySetException(new TimeoutException(string.Format("Timeout waiting for a message on queue [{0}]", m_outputQueue.QueueName)));
                }
            }

            return await tcs.Task;
        }

        public void Cancel()
        {
            m_stopToken.Cancel();
        }

        private static void LogErrors(Task task)
        {
            if (task == null)
                return;
            task.ContinueWith(x =>
            {
                if (x.Exception != null)
                    x.Exception.Flatten().Handle(ex =>
                    {
                        Console.WriteLine(ex);
                        return true;
                    });
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task ReceiveMessagesAsync()
        {
            while (!m_stopToken.IsCancellationRequested)
            {
                var message = await m_outputQueue.ReceiveAsync(m_stopToken.Token);
                var id = message.CorrelationId;
                var label = message.Label;
                var data = (string) message.Body;

                LogErrors(Task.Run(async () =>
                {
                    TaskCompletionSource<string> tcs;

                    var tcsForBag = new TaskCompletionSource<bool>();
                    if (m_bag.TryAdd(id, tcsForBag))
                        await m_bag[id].Task;
                    m_bag.TryRemove(id, out tcsForBag);

                    if (m_items.TryRemove(id, out tcs))
                    {
                        m_semaphore.Release();
                        if (m_exceptionHandler != null && !string.IsNullOrEmpty(label) && label.Contains("ERROR"))
                            tcs.TrySetException(m_exceptionHandler(data));
                        else
                            tcs.TrySetResult(data);
                    }
                }));
            }
        }
    }
}