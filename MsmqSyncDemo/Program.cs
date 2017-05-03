using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Messaging;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace MsmqSyncDemo
{
    internal class Program
    {
        private const string InputQueue = "_msmqsync.input";
        private const string OutputQueue = "_msmqsync.output";
        private const int TimeoutSeconds = 10;
        private const int MaxItems = 50000;
        private const int MaxBuffer = 5000;
        private const bool UseLocalSyncByDefault = true;

        private static void Main(string[] args)
        {
            try
            {
                var useLocalSync = UseLocalSyncByDefault;
                if (args.Length > 0 && args[0] == "-r")
                    useLocalSync = false;
                else if (args.Length > 0 && args[0] == "-l")
                    useLocalSync = true;

                // Create Queues
                Helper.RecreateQueues(InputQueue, OutputQueue);

                var sw = Stopwatch.StartNew();
                Console.WriteLine("Running {0} Sync Demo. Press Esc to cancel...", useLocalSync ? "Local" : "Remote");
                RunDemo(useLocalSync);
                Console.WriteLine("\nDONE! Elapsed: {0}", sw.Elapsed);
            }
            catch (Exception e)
            {
                Console.WriteLine("\nEXCEPTION:");
                Console.WriteLine(e);
            }

            Console.WriteLine("\nPress Enter to exit...");
            Console.ReadLine();
        }

        private static void RunDemo(bool useLocalSync)
        {
            var cts = new CancellationTokenSource();
            var e = new ManualResetEventSlim();

            IMsmqProcessor processor;
            if (useLocalSync)
                processor = new MsmqSyncLocal(InputQueue, OutputQueue, TimeSpan.FromSeconds(TimeoutSeconds*MaxBuffer), null, MaxBuffer);
            else
                processor = new MsmqSyncRemote(InputQueue, OutputQueue, TimeSpan.FromSeconds(TimeoutSeconds), null);

            var task = Task.Run(async () => await RunDemoAsync(processor, cts.Token));

            task.ContinueWith(t => Console.WriteLine(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
            task.ContinueWith(t => Console.WriteLine("\nTask has been cancelled"), TaskContinuationOptions.OnlyOnCanceled);
            task.ContinueWith(t => processor.Cancel());
            task.ContinueWith(t => e.Set());

            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                    if (Console.KeyAvailable && Console.ReadKey().Key == ConsoleKey.Escape)
                        cts.Cancel();
            });

            e.Wait();

            cts.Cancel();
        }

        private static async Task RunDemoAsync(IMsmqProcessor processor, CancellationToken cancellationToken)
        {
            var stopTokenSource = new CancellationTokenSource();

            cancellationToken.Register(stopTokenSource.Cancel);

            var options = new ExecutionDataflowBlockOptions {CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount*2};

            var workersBlock = new ActionBlock<int>(x => RunWorkerAsync(x, stopTokenSource.Token), options);

            var processBlock = new ActionBlock<string>(data => processor.ProcessAsync(data, cancellationToken), options);

            // Execute Workers
            foreach (var worker in Enumerable.Range(1, Environment.ProcessorCount))
                workersBlock.Post(worker);
            workersBlock.Complete();

            // Process Items
            foreach (var item in Enumerable.Range(1, MaxItems))
                processBlock.Post(item.ToString(CultureInfo.InvariantCulture));
            processBlock.Complete();

            processBlock.Completion.ContinueWith(t => stopTokenSource.Cancel());

            await Task.WhenAll(processBlock.Completion, workersBlock.Completion);
        }

        private static async Task RunWorkerAsync(int workerIndex, CancellationToken cancellationToken)
        {
            var inputQueue = new MessageQueue(string.Format(@".\private$\{0}", InputQueue), QueueAccessMode.Receive)
            {
                Formatter = new ActiveXMessageFormatter(),
                MessageReadPropertyFilter = {Id = true, Body = true}
            };

            var outputQueue = new MessageQueue(string.Format(@".\private$\{0}", OutputQueue), QueueAccessMode.Send)
            {
                Formatter = new ActiveXMessageFormatter()
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await inputQueue.ReceiveAsync(cancellationToken);
                var data = (string) message.Body;

                try
                {
                    // Process Data
                    var intData = int.Parse(data);
                    var result = Math.Sqrt(intData).ToString(CultureInfo.InvariantCulture);
                    outputQueue.Send(new Message(result, new ActiveXMessageFormatter())
                    {
                        CorrelationId = message.Id,
                        Label = string.Format("Worker {0}", workerIndex)
                    });
                }
                catch (Exception ex)
                {
                    outputQueue.Send(new Message(ex.ToString(), new ActiveXMessageFormatter())
                    {
                        CorrelationId = message.Id,
                        Label = string.Format("ERROR: Worker {0}", workerIndex)
                    });
                }
            }
        }
    }
}