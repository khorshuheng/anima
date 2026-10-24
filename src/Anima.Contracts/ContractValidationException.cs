namespace Anima.Contracts;

public sealed class ContractValidationException(string message) : Exception(message);
