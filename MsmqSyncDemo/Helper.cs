using System;
using System.Linq;
using System.Messaging;
using System.Threading;
using System.Threading.Tasks;

namespace MsmqSyncDemo
{
    public static class Helper
    {
        public static void RecreateQueues(params string[] queues)
        {
            DeleteQueues(queues);
            CreateQueuesIfNotExists(queues);
        }

        public static void CreateQueuesIfNotExists(params string[] queues)
        {
            if (queues != null)
                foreach (var queue in queues.Select(x => string.Format(@".\private$\{0}", x)).Where(x => !MessageQueue.Exists(x)))
                    MessageQueue.Create(queue);
        }

        public static void DeleteQueues(params string[] queues)
        {
            if (queues != null)
                foreach (var queue in queues.Select(x => string.Format(@".\private$\{0}", x)).Where(MessageQueue.Exists))
                    MessageQueue.Delete(queue);
        }

        private static readonly Func<MessageQueue, string, TimeSpan, Message> ReceiveByCorrelationIdFunc = (q, cid, tout) => q.ReceiveByCorrelationId(cid, tout);

        public static async Task<Message> ReceiveByCorrelationIdAsync(this MessageQueue queue, string correlationId, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
                try
                {
                    return await Task.Factory.FromAsync<Message>(
                        ReceiveByCorrelationIdFunc.BeginInvoke(queue, correlationId, TimeSpan.FromSeconds(1), ar => { }, null),
                        ReceiveByCorrelationIdFunc.EndInvoke);
                }
                catch (MessageQueueException ex)
                {
                    if (ex.MessageQueueErrorCode != MessageQueueErrorCode.IOTimeout)
                        throw;
                }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        public static async Task<Message> ReceiveAsync(this MessageQueue queue, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    return await Task.Factory.FromAsync<Message>(queue.BeginReceive(TimeSpan.FromSeconds(1)), queue.EndReceive);
                }
                catch (MessageQueueException ex)
                {
                    if (ex.MessageQueueErrorCode != MessageQueueErrorCode.IOTimeout)
                        throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}