namespace Anima.Knowledge;

public class KnowledgeException(string message) : Exception(message);

public sealed class KnowledgeValidationException(string message) : KnowledgeException(message);

public sealed class KnowledgeNotFoundException(string message) : KnowledgeException(message);
