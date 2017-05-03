<h1>Efficient Message Correlation in MSMQ</h1>

<h2>Introduction</h2>

<p>Let&#39;s assume that we need to interface with 3rd party system through <a href="http://en.wikipedia.org/wiki/Microsoft_Message_Queuing">MSMQ</a> by sending messages and receiving response back. Let&#39;s also assume that we need to do so within single execution context, meaning we would like to do some work locally, then send a message to 3rd party module via MSMQ, receive a response, and continue local processing. This can be achieved using .NET&#39;s <code>async</code>/<code>await</code> pattern together with MSMQ&#39;s <a href="https://msdn.microsoft.com/en-us/library/System.Messaging.MessageQueue.ReceiveByCorrelationId(v=vs.110).aspx"> <code>MessageQueue.ReceiveByCorrelationId</code></a> method - after sending a message, we can remember it&#39;s ID and wait for resulting message by calling <code>ReceiveByCorrelationId</code> and passing that ID as an argument.</p>

<p>In this article I will demonstrate two different approaches of retrieving messages from MSMQ by Correlation ID. First approach, I call it &quot;<strong>Remote Sync</strong>&quot;, is a standard way suggested by MSMQ API via <code>MessageQueue.ReceiveByCorrelationId</code> method. The second approach, I call it &quot;<strong>Local Sync</strong>&quot;, is custom implementation of&nbsp;<code>ReceiveByCorrelationId</code> that internally uses MSMQ&#39;s <a href="https://msdn.microsoft.com/en-us/library/system.messaging.messagequeue.receive(v=vs.110).aspx"> <code>MessageQueue.Receive</code></a> method and resolves message correlation locally.</p>

<p>In this article I will not cover the basics of MSMQ, assuming that you are already familiar with main concepts of Message Oriented Design. There are plenty of good articles regarding the topic on <a href="http://www.codeproject.com/search.aspx?q=MSMQ&amp;doctypeid=1%3b2%3b3"> Code Project</a>. In addition, the following article describes <a href="https://support.microsoft.com/en-us/kb/555298">how to correlate request/response messages by using System.Messaging</a>.</p>

<h2>Why?</h2>

<p>In general, when designing Message Oriented System, you should avoid using synchronous <code>Send</code>/<code>ReceiveByCorrelationId</code> technique in favor of using an asynchronous <code>Send</code>/<code>Receive</code>. It may sound simple, though, it can actually change the whole architecture dramatically, because you would have to break your execution context apart and have Sender and Receiver as separate independent units. It leads to the fundamental question: in the application, when user clicks a &quot;Calc&quot; button, do you block current thread while waiting for response, or, setup a callback that would fire upon receiving response, and let user continue to work with the app? First approach is easy, but less scalable (blocking UI), while the second one is more difficult to implement, though it is more user-friendly and better scalable (responsive UI).</p>

<p>It is a known fact that <code>ReceiveByCorrelationId</code> method (same applies to <code>ReceiveByLookupID</code> and <code>ReceiveById</code>) is a performance killer when you have many parallel threads waiting for response and big number of messages being pushed through MSMQ. This is due to the nature of how correlation resolution is implemented internally in that method. Under the hood, it uses a <code>Cursor</code> that would <code>Peek</code> all the messages in the queue till it finds the message with matching Correlation ID. When it finds the message, it would call <code>Receive</code> method to receive it. When there are too many threads constantly iterating over the cursor, performance goes down very quickly. According to <a href="https://support.microsoft.com/en-us/kb/555298">Microsoft</a>:</p>

<p><strong>Important Note</strong>: <code>ReceiveByCorrelationId</code> uses sequence search to find a message in a queue. Use it with carefulness; it might be inefficient when a high number of messages reside in a queue.</p>

<p>In the alternative approach, we will maintain internal <code>Dictionary</code> of all message IDs that we send, and for every message picked from the queue, have the receiving thread look for the matching IDs in that <code>Dictionary</code>. The downside of this approach is that the information about all messages being processed is stored in-memory, and can be lost if the server goes down.</p>

<h2>Demo Application</h2>

<p>The demo app is a console application accepting two possible arguments: <code>-l</code> (Local Sync) and <code>-r</code> (Remote Sync). If no arguments were specified, Local Sync will be used by default.</p>

<p>There are several constants hard-coded in the app that you can alter if needed (see private constants in Program.cs):</p>

<ul>
	<li><code>InputQueue</code>, <code>OutputQueue</code>: The names of demo input and output queues</li>
	<li><code>TimeoutSeconds</code>: Timeout in seconds of wait time for receiving response. Note that in Remote Sync, the timeout is used for every message as defined; in Local Sync, since messages are being sent in bulk, the timeout is multiplied by the number of messages being sent at once.</li>
	<li><code>MaxItems</code>: The number of messages used in the demo (50,000 by default).</li>
	<li><code>MaxBuffer</code>: The number of messages being sent to MSQM at once in Local Sync scenario.</li>
	<li><code>UseLocalSyncByDefault</code>: Flag indicating whether to use Local Sync demo by default.</li>
</ul>

<p>Depending on current Processor Type, the app will instantiate either <code>MsmqSyncLocal</code>, for Local Synch Demo, or <code>MsmqSyncRemote</code>, for Remote Sync Demo. Both of them have asynchronous <code>ProcessAsync</code> method accepting <code>string</code> data along with cancellation token, and returning <code>Task&lt;string&gt;</code>. This method is supposed to send data to an input queue, wait, and receive the result back.</p>

<p>As a next step, the app will run several (per number of CPU cores) worker threads emulating this 3rd party processor I mentioned earlier. The job of each Worker is to continuously retrieve incoming messages from an input queue, convert them to integers, calculate the square root, and send the result to an output queue. The code of worker method is shown below:</p>

<pre lang="cs">
private static async Task RunWorkerAsync(int workerIndex, CancellationToken cancellationToken)
{
    var inputQueue = new MessageQueue(string.Format(@&quot;.\private$\{0}&quot;, InputQueue), QueueAccessMode.Receive)
    {
        Formatter = new ActiveXMessageFormatter(),
        MessageReadPropertyFilter = {Id = true, Body = true}
    };

    var outputQueue = new MessageQueue(string.Format(@&quot;.\private$\{0}&quot;, OutputQueue), QueueAccessMode.Send)
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
                Label = string.Format(&quot;Worker {0}&quot;, workerIndex)
            });
        }
        catch (Exception ex)
        {
            outputQueue.Send(new Message(ex.ToString(), new ActiveXMessageFormatter())
            {
                CorrelationId = message.Id,
                Label = string.Format(&quot;ERROR: Worker {0}&quot;, workerIndex)
            });
        }
    }
}</pre>

<h2>Remote Sync</h2>

<p>As mentioned before, Remote Sync Processor uses <code>ReceiveByCorrelationId</code> method to&nbsp;retrieve messages from output queue. Here is the code that demonstrates it:</p>

<pre lang="cs">
public async Task&lt;string&gt; ProcessAsync(string data, CancellationToken ct)
{
    var inputQueue = new MessageQueue(string.Format(@&quot;.\private$\{0}&quot;, m_inputQueue), QueueAccessMode.Send)
    {
        Formatter = ActiveXFormatter
    };

    var outputQueue = new MessageQueue(string.Format(@&quot;.\private$\{0}&quot;, m_outputQueue), QueueAccessMode.Receive)
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
        var resultMessage = await outputQueue.<strong>ReceiveByCorrelationIdAsync</strong>(id, ct);

        var label = resultMessage.Label;
        var result = (string) resultMessage.Body;

        if (m_exceptionHandler != null &amp;&amp; !string.IsNullOrEmpty(label) &amp;&amp; label.Contains(&quot;ERROR&quot;))
            throw m_exceptionHandler(data);

        return result;
    }
    catch (Exception)
    {
        if (!ct.IsCancellationRequested)
            throw;
        return null;
    }
}</pre>

<p>Note that I am not using <code>ReceiveByCorrelationId</code> directly, but via an extension method called <code>ReceiveByCorrelationIdAsync</code>. This is due to the fact that original <code>ReceiveByCorrelationId</code> method does not properly work with timeouts and does not support cancellation tokens.</p>

<h2>Local Sync</h2>

<p>In the Local Sync scenario, the <code>ProcessAsync</code> method has identical signature. However, this time, it is truly asynchronous:</p>

<pre lang="cs">
public async Task&lt;string&gt; ProcessAsync(string data, CancellationToken ct)
{
    await m_semaphore.WaitAsync(ct);

    var tcs = new TaskCompletionSource&lt;string&gt;();
    var message = new Message(data, ActiveXFormatter);

    m_inputQueue.Send(message);

    var id = message.Id;

    m_items.TryAdd(id, tcs);

    var tcsForBag = new TaskCompletionSource&lt;bool&gt;();
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
                tcs.TrySetException(new TimeoutException(string.Format(&quot;Timeout waiting for a message on queue [{0}]&quot;, m_outputQueue.QueueName)));
        }
    }

    return await tcs.Task;
}</pre>

<p>An instance of <code>MsmqSyncLocal</code> class internally maintains a <code>SemaphoreSlim</code> that controls the number of messages we send to MSMQ at once. This number can be configured by <code>MaxItems</code> constant.</p>

<p>When <code>ProcessAsync</code> is called, it creates a <code>Message</code> and immediately sends it to the input queue. Then it creates a <code>TaskCompletionSource</code> object, whose <code>Task</code> property we will asynchronously return back to the caller. This completion source object as well as the message ID that we sent earlier are added to an internal <code>ConcurrentDictionary</code> called <code>m_items</code>.</p>

<p>Another method <code>ReceiveMessagesAsync</code>, that is being executed from constructor, runs an infinite loop retrieving messages from the output queue. For every message received, it checks its <code>CorrelationId</code> in the <code>m_items</code> dictionary. If the match is found, it would signal the <code>TaskCompletionSource</code> associated with it setting either Result, if successful, or Cancellation/Error.</p>

<pre lang="cs">
private async Task ReceiveMessagesAsync()
{
    while (!m_stopToken.IsCancellationRequested)
    {
        var message = await m_outputQueue.ReceiveAsync(m_stopToken.Token);
        var id = message.CorrelationId;
        var label = message.Label;
        var data = (string) message.Body;

        LogErrors(Task.Run(async () =&gt;
        {
            TaskCompletionSource&lt;string&gt; tcs;

            var tcsForBag = new TaskCompletionSource&lt;bool&gt;();
            if (m_bag.TryAdd(id, tcsForBag))
                await m_bag[id].Task;
            m_bag.TryRemove(id, out tcsForBag);

            if (m_items.TryRemove(id, out tcs))
            {
                m_semaphore.Release();
                if (m_exceptionHandler != null &amp;&amp; !string.IsNullOrEmpty(label) &amp;&amp; label.Contains(&quot;ERROR&quot;))
                    tcs.TrySetException(m_exceptionHandler(data));
                else
                    tcs.TrySetResult(data);
            }
        }));
    }
}</pre>

<p>You may have noticed that there is one more <code>ConcurrentDictionary</code> involved (<code>m_bag</code>). It solves the problem of condition racing that may occur in case if <code>ReceiveMessagesAsync</code> receives a message before it&#39;s ID has been added to <code>m_items</code>. If that happens, we won&#39;t be able to find expected correlation ID in the dictionary.</p>

<p>In fact, if we knew the <code>Message ID</code> prior to sending it, we could have simply populated <code>m_items</code> prior to sending the message, hence avoiding the race condition. However, this is not possible in MSMQ.</p>

<p>So, in order to coordinate message ID insertion, <code>ProcessAsync</code> would attempt to insert a <code>TaskCompletionSource</code> object along with that ID into the <code>m_bag</code>. If successful, we don&#39;t have to do anything else because that element would be removed by <code>ReceiveMessagesAsync</code>. Otherwise, if that ID already exists in the <code>m_bag</code>, which means that it has already been added by <code>ReceiveMessagesAsync</code>, we would signal its completion in order to let receiving process know that it can continue execution.</p>

<h2>Summary</h2>

<p>My testing indicated that <strong>Remote Sync</strong> is taking approximately <strong>65 seconds</strong> as oppose to <strong>Local Sync</strong> taking around <strong>5 seconds</strong> in the same environment.</p>

<p>Being more than <strong>10 times</strong> slower than it could, <code>ReceiveByCorrelationId</code> method should be avoided as much as possible. If asynchronous workflow does not fit into your application architecture and performance is a concern, I believe local correlation is something to consider as an option.</p>
