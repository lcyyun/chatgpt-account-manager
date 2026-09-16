using System.Net;

namespace GptPlusManager.Core.Models;

public record OperationResult(
    bool Succeeded,
    string Message,
    string? ErrorCode = null,
    HttpStatusCode? StatusCode = null)
{
    public static OperationResult Success(string message = "") => new(true, message);

    public static OperationResult Failure(
        string message,
        string? errorCode = null,
        HttpStatusCode? statusCode = null) => new(false, message, errorCode, statusCode);
}

public sealed record OperationResult<T>(
    bool Succeeded,
    T? Value,
    string Message,
    string? ErrorCode = null,
    HttpStatusCode? StatusCode = null)
{
    public static OperationResult<T> Success(T value, string message = "") =>
        new(true, value, message);

    public static OperationResult<T> Failure(
        string message,
        string? errorCode = null,
        HttpStatusCode? statusCode = null) =>
        new(false, default, message, errorCode, statusCode);

    public static OperationResult<T> Failure(
        T value,
        string message,
        string? errorCode = null,
        HttpStatusCode? statusCode = null) =>
        new(false, value, message, errorCode, statusCode);
}
