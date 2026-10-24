namespace Anima.Workflow;

public class WorkflowException(string message) : Exception(message);

public sealed class WorkflowNotFoundException(string message) : WorkflowException(message);

public sealed class WorkflowValidationException(string message) : WorkflowException(message);
