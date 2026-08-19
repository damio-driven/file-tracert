using FileTracert.Contracts.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FileTracert.Host.Infrastructure;

/// <summary>
/// Turns the queue's domain exceptions into HTTP answers, in one place for every action of
/// <c>OperationsController</c> (K11).
///
/// <para>It replaces six copies of the same try/catch, two of which decided between 404 and 400
/// with <c>ex.Message.Contains("not found")</c> — string-sniffing that a reworded (or translated)
/// sentence breaks silently. The distinction is now carried by the type:
/// <see cref="EntityNotFoundException"/> is a 404, everything else the queue rejects is a 400.</para>
///
/// <para>§9 wants both halves: the message reaches the client AND the exception is logged whole —
/// stack and inner exception included, which only exist here. Before this, only <c>Enqueue</c> and
/// <c>EnqueueBatch</c> logged; the other four converted an exception into a status code and said
/// nothing about it anywhere.</para>
///
/// <para>The caught set is exactly what the actions caught before, <c>ArgumentException</c> and
/// <c>InvalidOperationException</c>. That keeps the mapping identical, and it keeps a known
/// blemish: <c>ObjectDisposedException</c> derives from <c>InvalidOperationException</c> and would
/// read as a 400. It did before too — widening or narrowing the set is a behaviour change and does
/// not belong in a dedup commit.</para>
/// </summary>
public sealed class QueueExceptionFilter : IExceptionFilter
{
    private readonly ILogger<QueueExceptionFilter> _logger;

    public QueueExceptionFilter(ILogger<QueueExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        var action = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName ?? "?";
        var request = context.HttpContext.Request;

        switch (context.Exception)
        {
            case EntityNotFoundException notFound:
                _logger.LogWarning(notFound,
                    "{Action} ({Method} {Path}): {Entity} {Id} does not exist.",
                    action, request.Method, request.Path, notFound.Entity, notFound.Id);
                Answer(context, new NotFoundObjectResult(new { error = notFound.Message }));
                break;

            case ArgumentException or InvalidOperationException:
                _logger.LogWarning(context.Exception,
                    "{Action} ({Method} {Path}) rejected as invalid.",
                    action, request.Method, request.Path);
                Answer(context, new BadRequestObjectResult(new { error = context.Exception.Message }));
                break;
        }
    }

    private static void Answer(ExceptionContext context, IActionResult result)
    {
        context.Result = result;
        context.ExceptionHandled = true;
    }
}
