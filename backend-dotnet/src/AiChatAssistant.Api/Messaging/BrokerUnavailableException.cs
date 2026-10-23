namespace AiChatAssistant.Api.Messaging;

/// <summary>RabbitMQ did not answer in time (paused, partitioned, or overloaded). Callers map it to "try again shortly", never to a 500.</summary>
public sealed class BrokerUnavailableException : Exception
{
    public BrokerUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
