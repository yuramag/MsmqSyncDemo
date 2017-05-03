using System;
using System.Messaging;
using System.Threading;
using System.Threading.Tasks;

namespace MsmqSyncDemo
{
    public sealed class MsmqSyncRemote : IMsmqProcessor
    {
        private static readonly ActiveXMessageFormatter ActiveXFormatter = new ActiveXMessageFormatter();

        private readonly TimeSpan m_timeout;
        private readonly string m_inputQueue;
        private readonly string m_outputQueue;
        private readonly Func<string, Exception> m_exceptionHandler;

        public MsmqSyncRemote(string inputQueue, string outputQueue, TimeSpan timeout, Func<string, Exception> exceptionHandler)
        {
            m_exceptionHandler = exceptionHandler;
            m_inputQueue = inputQueue;
            m_outputQueue = outputQueue;
            m_timeout = timeout;
        }

        public void Cancel()
        {
        }

        public async Task<string> ProcessAsync(string data, CancellationToken ct)
        {
            var inputQueue = new MessageQueue(string.Format(@".\private$\{0}", m_inputQueue), QueueAccessMode.Send)
            {
                Formatter = ActiveXFormatter
            };

            var outputQueue = new MessageQueue(string.Format(@".\private$\{0}", m_outputQueue), QueueAccessMode.Receive)
            {
                Formatter = ActiveXFormatter,
                MessageReadPropertyFilter = {Id = true, CorrelationId = true, Body = true, Label = true}
            };

            var message = new Message(data, ActiveXFormatter);
            inputQueue.Send(message);
            var id = message.Id;

            try
            {
                //var resultMessage = outputQueue.ReceiveByCorrelationId(id, m_timeout);
                var resultMessage = await outputQueue.ReceiveByCorrelationIdAsync(id, ct);

                var label = resultMessage.Label;
                var result = (string) resultMessage.Body;

                if (m_exceptionHandler != null && !string.IsNullOrEmpty(label) && label.Contains("ERROR"))
                    throw m_exceptionHandler(data);

                return result;
            }
            catch (Exception)
            {
                if (!ct.IsCancellationRequested)
                    throw;
                return null;
            }
        }
    }
}