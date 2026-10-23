using AiChatAssistant.Api.Services;
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
            services.AddSingleton<RabbitMqConnection>();
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
}
