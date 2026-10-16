using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignDesk.Domain;
using SignDesk.Infrastructure.Documents;

namespace SignDesk.Api;

public sealed class ErrorHandling(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, exception.Message, "not_found"),
            DomainException d => (StatusCodes.Status409Conflict, d.Message, d.Code),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The document was changed by another request. Reload and retry.", "concurrency_conflict"),
            _ => (0, string.Empty, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        var details = new ProblemDetails { Status = status, Title = title };
        details.Extensions["code"] = code;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = details, Exception = exception });
    }
}
