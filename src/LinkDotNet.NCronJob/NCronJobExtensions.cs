using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;

namespace LinkDotNet.NCronJob;

/// <summary>
/// Extensions for the <see cref="IServiceCollection"/> to add cron jobs.
/// </summary>
public static class NCronJobExtensions
{
    /// <summary>
    /// Adds NCronJob services to the service container.
    /// </summary>
    /// <param name="services">The service collection used to register the services.</param>
    /// <param name="options">The builder to register jobs and other settings.</param>
    /// <param name="configuration">The configuration to use for the settings.</param>
    /// <example>
    /// To register a job that runs once every hour with a parameter and a handler that gets notified once the job is completed:
    /// <code>
    /// Services.AddNCronJob(options =>
    ///  .AddJob&lt;MyJob&gt;(c => c.WithCronExpression("0 * * * *").WithParameter("myParameter"))
    ///  .AddNotificationHandler&lt;MyJobHandler, MyJob&gt;());
    /// </code>
    /// </example>
    public static IServiceCollection AddNCronJob(
        this IServiceCollection services,
        Action<NCronJobOptionBuilder>? options = null,
        IConfiguration? configuration = null)
    {
        var concurrencySettings = new ConcurrencySettings();
        configuration?.GetSection("NCronJob:Concurrency").Bind(concurrencySettings);
        services.AddSingleton(concurrencySettings);

        var builder = new NCronJobOptionBuilder(services, concurrencySettings);
        options?.Invoke(builder);

        services.AddHostedService<CronScheduler>();
        services.AddSingleton<CronRegistry>();
        services.AddSingleton<JobExecutor>();
        services.TryAddSingleton<IRetryHandler, RetryHandler>();
        services.AddSingleton<IInstantJobRegistry>(c => c.GetRequiredService<CronRegistry>());
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// Adds NCronJob services to the application builder and configures settings using the application's configuration.
    /// </summary>
    /// <param name="builder">The <see cref="WebApplicationBuilder"/> to configure.</param>
    /// <param name="options">An action to configure the NCronJob options.</param>
    /// <returns>The <see cref="WebApplicationBuilder"/> to allow for chaining of calls.</returns>
    /// <remarks>
    /// This extension method binds the NCronJob related settings from the application's configuration and registers the necessary services.
    /// It simplifies the process of adding and configuring NCronJob in an ASP.NET Core application by handling configuration binding internally.
    /// </remarks>
    /// <example>
    /// Example of adding NCronJob to a WebApplicationBuilder with default settings:
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddNCronJob(n => n.AddJob&lt;PrintHelloWorldJob&gt;(p => p.WithCronExpression("*/2 * * * *")));
    /// var app = builder.Build();
    /// app.Run();
    /// </code>
    /// </example>
    public static WebApplicationBuilder AddNCronJob(this WebApplicationBuilder builder, Action<NCronJobOptionBuilder>? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var configuration = builder.Configuration;

        var concurrencySettings = new ConcurrencySettings();
        configuration?.GetSection("NCronJob:Concurrency").Bind(concurrencySettings);
        services.AddSingleton(concurrencySettings);

        var jobOptionBuilder = new NCronJobOptionBuilder(services, concurrencySettings);
        options?.Invoke(jobOptionBuilder);

        services.AddHostedService<CronScheduler>();
        services.AddSingleton<CronRegistry>();
        services.AddSingleton<JobExecutor>();
        services.TryAddSingleton<IRetryHandler, RetryHandler>();
        services.AddSingleton<IInstantJobRegistry>(c => c.GetRequiredService<CronRegistry>());
        services.TryAddSingleton(TimeProvider.System);

        return builder;
    }
}
