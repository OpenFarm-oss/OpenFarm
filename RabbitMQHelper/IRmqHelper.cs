using RabbitMQHelper.MessageTypes;

namespace RabbitMQHelper;

public interface IRmqHelper
{
    /// <summary>
    /// Attempts a connection to RabbitMQ. Must be done before using the helper.
    /// </summary>
    Task<bool> Connect();

    /// <summary>
    /// Attaches a synchronous listener to the provided queue.
    /// </summary>
    /// <param name="queue">Queue to attach listener to.</param>
    /// <param name="listener">Synchronous listener that receives the deserialized message and returns success/failure.</param>
    /// <returns><c>true</c> if listener was added successfully; <c>false</c> if connection is unavailable.</returns>
    /// <remarks>
    /// Consider using <see cref="AddListenerAsync{T}"/> for async message handlers to avoid blocking the thread pool.
    /// </remarks>
    bool AddListener<T>(QueueNames queue, Func<T, bool> listener) where T : Message;

    /// <summary>
    /// Attaches an asynchronous listener to the provided queue.
    /// </summary>
    /// <param name="queue">Queue to attach listener to.</param>
    /// <param name="listener">Async listener that receives the deserialized message and returns success/failure.</param>
    /// <returns><c>true</c> if listener was added successfully; <c>false</c> if connection is unavailable.</returns>
    /// <remarks>
    /// This is the preferred method for handlers that perform async operations (database access, HTTP calls, etc.)
    /// as it properly awaits the handler without blocking threads.
    /// </remarks>
    bool AddListenerAsync<T>(QueueNames queue, Func<T, Task<bool>> listener) where T : Message;

    /// <summary>
    /// Queues the message to go to the specified exchange.
    /// </summary>
    /// <param name="exchange"> Exchange to publish message to. </param>
    /// <param name="message"> Message to serialize and publish to exchange. </param>
    /// <returns></returns>
    Task QueueMessage(ExchangeNames exchange, Message message);

    /// <summary>
    /// Queues the message to go to the specified exchange.
    /// </summary>
    /// <param name="exchange"> Exchange to publish message to. </param>
    /// <param name="message"> Message to serialize and publish to exchange. </param>
    /// <returns></returns>
    Task QueueMessage(ExchangeNames exchange, RejectMessage message);

    /// <summary>
    /// Queues the message to go to the specified exchange.
    /// </summary>
    /// <param name="exchange"> Exchange to publish message to. </param>
    /// <param name="message"> Message to serialize and publish to exchange. </param>
    /// <returns></returns>
    Task QueueMessage(ExchangeNames exchange, AcceptMessage message);

    /// <summary>
    /// Queues the message to go to the specified exchange.
    /// </summary>
    /// <param name="exchange"> Exchange to publish message to. </param>
    /// <param name="message"> Message to serialize and publish to exchange. </param>
    /// <returns></returns>
    Task QueueMessage(ExchangeNames exchange, PrintStartedMessage message);

    /// <summary>
    /// Queues the message to go to the specified exchange.
    /// </summary>
    /// <param name="exchange"> Exchange to publish message to. </param>
    /// <param name="message"> Message to serialize and publish to exchange. </param>
    /// <returns></returns>
    Task QueueMessage(ExchangeNames exchange, PrintFinishedMessage message);

    /// <summary>
    /// Queues the message to go to the specified exchange.
    /// </summary>
    /// <param name="exchange"> Exchange to publish message to. </param>
    /// <param name="message"> Message to serialize and publish to exchange. </param>
    /// <returns></returns>
    Task QueueMessage(ExchangeNames exchange, PrintClearedMessage message);

    /// <summary>
    /// Queues the message to go to the specified exchange.
    /// </summary>
    /// <param name="exchange"> Exchange to publish message to. </param>
    /// <param name="message"> Message to serialize and publish to exchange. </param>
    /// <returns></returns>
    Task QueueMessage(ExchangeNames exchange, OperatorReplyMessage message);

    void Dispose();
    ValueTask DisposeAsync();
    bool IsConnected();
}
