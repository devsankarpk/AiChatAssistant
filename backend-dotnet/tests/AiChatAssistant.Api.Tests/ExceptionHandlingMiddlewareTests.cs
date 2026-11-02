using System.Text.Json;
using AiChatAssistant.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiChatAssistant.Api.Tests;

public class ExceptionHandlingMiddlewareTests
{
    private static async Task<DefaultHttpContext> Run(RequestDelegate next)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new ExceptionHandlingMiddleware(next, NullLogger<ExceptionHandlingMiddleware>.Instance).InvokeAsync(context);
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task AnUnhandledException_Becomes500_InTheStandardShape()
    {
        var context = await Run(_ => throw new InvalidOperationException("db password is hunter2"));

        Assert.Equal(500, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        using var doc = JsonDocument.Parse(Body(context));
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("internal_error", error.GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheResponse_NeverLeaksTheExceptionTextOrAStackTrace()
    {
        var context = await Run(_ => throw new InvalidOperationException("db password is hunter2"));

        var body = Body(context);
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("   at ", body);
    }

    [Fact]
    public async Task ARequestThatSucceeds_IsLeftAlone()
    {
        var context = await Run(async c => { c.Response.StatusCode = 201; await c.Response.WriteAsync("fine"); });

        Assert.Equal(201, context.Response.StatusCode);
        Assert.Equal("fine", Body(context));
    }

    [Fact]
    public async Task APartiallyWrittenResponse_IsDiscarded_NotMixedWithTheError()
    {
        var context = await Run(async c =>
        {
            c.Response.Headers["X-Half"] = "1";
            await c.Response.WriteAsync("partial");
            throw new Exception("boom");
        });

        Assert.DoesNotContain("partial", Body(context));
        Assert.Equal(500, context.Response.StatusCode);
    }
}
