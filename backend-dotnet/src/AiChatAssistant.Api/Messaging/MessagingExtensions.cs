using AiChatAssistant.Api.Services;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiChatAssistant.Api.Messaging;

public static class MessagingExtensions
{
    /// <summary>
    /// Registers the email path. With RabbitMq:Host set: a shared connection, the queue publisher and the
    /// background worker. Without it: the inline sender, so the app runs with only MySQL.
    /// Which <see cref="IEmailSender"/> actually delivers is chosen by Email:Provider.
    /// </summary>
    public static IServiceCollection AddEmailDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        var rabbit = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        rabbit.Validate();

        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.Configure<EmailQueueOptions>(configuration.GetSection(EmailQueueOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        var provider = configuration.GetSection(EmailQueueOptions.SectionName).Get<EmailQueueOptions>()?.Provider ?? "smtp";
        if (string.Equals(provider, "console", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IEmailSender, ConsoleEmailSender>();
        }
        else if (string.Equals(provider, "smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            throw new InvalidOperationException($"Email:Provider '{provider}' is not supported. Use 'smtp' or 'console'.");
        }

        if (rabbit.Enabled)
        {
            services.TryAddSingleton<RabbitMqConnection>();
            services.AddSingleton<IEmailQueue, RabbitMqEmailQueue>();
            services.AddScoped<PasswordResetEmailProcessor>();
            services.AddHostedService<EmailSendWorker>();
        }
        else
        {
            services.AddScoped<IEmailQueue, InlineEmailQueue>();
        }

        return services;
    }

    /// <summary>
    /// Phases 18 and 19, both on top of RabbitMQ. <b>Async AI replies</b> (<c>Chat:AsyncReplies</c>, default off): the send
    /// request queues a job and answers 202, and a worker produces the reply. <b>Direct-chat events</b>: with a broker, every
    /// direct-chat change also writes an outbox row in the same transaction, a dispatcher publishes it to the
    /// <c>direct.events</c> exchange and a subscriber logs and counts it. Without a broker neither exists and nothing is written.
    /// </summary>
    public static IServiceCollection AddBrokerFeatures(this IServiceCollection services, IConfiguration configuration)
    {
        var rabbit = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        var chat = configuration.GetSection(ChatOptions.SectionName).Get<ChatOptions>() ?? new ChatOptions();
        var outbox = configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>() ?? new OutboxOptions();

        services.Configure<ChatOptions>(configuration.GetSection(ChatOptions.SectionName));
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IChatNotifier, SignalRChatNotifier>();

        if (chat.AsyncReplies)
        {
            if (!rabbit.Enabled)
            {
                throw new InvalidOperationException(
                    "Chat:AsyncReplies is on but RabbitMq:Host is not set. Async replies need RabbitMQ: set RabbitMq:Host (and its "
                    + "credentials) or turn Chat:AsyncReplies off.");
            }

            services.TryAddSingleton<RabbitMqConnection>();
            services.AddSingleton<IAiReplyQueue, RabbitMqAiReplyQueue>();
            services.AddScoped<AiReplyProcessor>();
            services.AddHostedService<AiReplyWorker>();
        }

        if (rabbit.Enabled && outbox.Enabled)
        {
            services.TryAddSingleton<RabbitMqConnection>();
            services.AddScoped<IOutbox, OutboxWriter>();
            services.AddSingleton<IOutboxPublisher, RabbitMqOutboxPublisher>();
            // Shared memory of handled events when Redis is configured (the two copies of a repeated event can land on
            // different replicas); per-process otherwise. Resolved lazily because Redis is registered elsewhere.
            services.AddSingleton<IEventDeduplicator>(sp => sp.GetService<IConnectionMultiplexer>() is { } redis
                ? new RedisEventDeduplicator(redis, sp.GetRequiredService<ILogger<RedisEventDeduplicator>>())
                : new InMemoryEventDeduplicator());
            services.AddSingleton<DirectEventHandler>();
            services.AddHostedService<OutboxDispatcher>();
            services.AddHostedService<DirectEventsConsumer>();
        }
        else
        {
            services.AddScoped<IOutbox, NoOpOutbox>();
        }

        return services;
    }
}
