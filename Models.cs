namespace DeliAi.Backend;

// Replaces jsons/deliInfo.json
public class FactEntity
{
  public int Id { get; set; }
  public string Info { get; set; } = "";
}

// Replaces jsons/archive.json
public class ArchivedFactEntity
{
  public int Id { get; set; }
  public string Info { get; set; } = "";
  public DateTime ArchivedAt { get; set; } = DateTime.UtcNow;
}

// Replaces jsons/unanswered-questions.json
public class UnansweredQuestionEntity
{
  public int Id { get; set; }
  public string Info { get; set; } = "";
  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Replaces jsons/flagged-messages.json
public class FlaggedMessageEntity
{
  public int Id { get; set; }
  public DateTime Timestamp { get; set; } = DateTime.UtcNow;
  public string Question { get; set; } = "";
  public string Reason { get; set; } = "";
}

// Replaces jsons/wrong-info-reports.json
public class WrongInfoReportEntity
{
  public int Id { get; set; }
  public string Response { get; set; } = "";
  public string Name { get; set; } = "";
  public bool FullyWrong { get; set; }
  public string HowToImprove { get; set; } = "";
  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}