using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DeliAi.Backend.Services;

public partial class DeliBot
{
  public async Task<List<object>> GetAllFactsAsync()
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();
    var facts = await db.Facts.Where(e => e.Info != DefaultRefusalRule).OrderBy(e => e.Id).ToListAsync();
    return facts
        .Select((e, i) => new { index = i, info = e.Info })
        .Cast<object>()
        .ToList();
  }

  public async Task<string> AddFactAsync(string info)
  {
    if (string.IsNullOrWhiteSpace(info) || info.Trim().Length < 5)
      return "Fact is too short or empty to save.";

    await using (var db = await _dbContextFactory.CreateDbContextAsync())
    {
      db.Facts.Add(new FactEntity { Info = info });
      await db.SaveChangesAsync();
    }

    await PruneUnansweredQuestionsAsync();

    return $"Saved: {info}";
  }

  public async Task<string> AddFactsBulkAsync(IEnumerable<string> infos)
  {
    var list = infos.ToList();

    await using (var db = await _dbContextFactory.CreateDbContextAsync())
    {
      foreach (var info in list)
        db.Facts.Add(new FactEntity { Info = info });

      await db.SaveChangesAsync();
    }

    await PruneUnansweredQuestionsAsync();
    await DedupeFactsAsync();

    return $"Added {list.Count} piece(s) of info.";
  }

  public async Task<string> EditFactByIndexAsync(int index, string newInfo)
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();
    var deletable = await db.Facts.Where(e => e.Info != DefaultRefusalRule).OrderBy(e => e.Id).ToListAsync();

    if (index < 0 || index >= deletable.Count)
      return "Invalid fact index.";

    var toEdit = deletable[index];
    toEdit.Info = newInfo;
    await db.SaveChangesAsync();

    return $"Updated: {newInfo}";
  }

  public async Task<string> DeleteFactByIndexAsync(int index)
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();
    var deletable = await db.Facts.Where(e => e.Info != DefaultRefusalRule).OrderBy(e => e.Id).ToListAsync();

    if (index < 0 || index >= deletable.Count)
      return "Invalid fact index.";

    var toRemove = deletable[index];

    db.ArchivedFacts.Add(new ArchivedFactEntity { Info = toRemove.Info });
    db.Facts.Remove(toRemove);
    await db.SaveChangesAsync();

    return $"Deleted: {toRemove.Info}";
  }

  private async Task DedupeFactsAsync()
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();

    var deletable = await db.Facts.Where(e => e.Info != DefaultRefusalRule).ToListAsync();
    if (deletable.Count < 2) return; // nothing to dedupe

    string factList = string.Join("\n", deletable.Select(e => $"- {e.Info}"));

    string systemPrompt =
     "You are helping maintain a deli assistant bot's knowledge base by cleaning up duplicate or " +
     "redundant facts.\n\n" +
     "You will be given the full CURRENT FACTS list. Your job is to find facts that duplicate the same " +
     "specific information as another fact in the list, and mark the worse/redundant copy for removal.\n\n" +
     "TWO FACTS ARE DUPLICATES if they state the same specific piece of information, even if the exact " +
     "wording differs. This includes differences in capitalization, punctuation, spacing, word order, or " +
     "phrasing. For example, all of the following pairs ARE duplicates:\n" +
     "- \"Blake is the manager\" and \"blake is the manager.\"\n" +
     "- \"Coleslaw is 4.99/lb\" and \"coleslaw costs $4.99 per pound\"\n" +
     "- \"The deli closes at 9pm\" and \"deli closing time is 9:00 PM\"\n\n" +
     "When you find a duplicate pair or group, keep exactly ONE version and mark ALL other copies for " +
     "removal. To choose which one to keep, prefer (in this order of priority):\n" +
     "1. The version with correct capitalization (proper nouns and the start of the sentence capitalized) " +
     "and correct punctuation.\n" +
     "2. The version that is more complete or contains more detail, as long as that detail is still " +
     "accurate.\n" +
     "3. The version that reads more clearly and naturally.\n" +
     "Never keep a version with worse capitalization or grammar over one that is otherwise equivalent but " +
     "better formatted.\n\n" +
     "TWO FACTS ARE NOT DUPLICATES if they cover the same general topic but state different, " +
     "non-overlapping information. For example, these are NOT duplicates:\n" +
     "- \"Turkey sandwich is $6.99\" and \"Ham sandwich is $6.99\" (different items)\n" +
     "- \"Ribs are in the back cooler\" and \"Prime rib is in the back cooler\" (different products)\n\n" +
     "If two facts directly CONTRADICT each other (e.g. two different prices for the exact same specific " +
     "item) and you cannot tell which one is more current or correct, do NOT remove either one — leave " +
     "both for a human to review instead. Only remove facts you are confident are true duplicates, not " +
     "genuinely conflicting information.\n\n" +
     $"CURRENT FACTS:\n{factList}\n\n" +
     "Respond with ONLY a JSON object shaped exactly like this, nothing else — no markdown, no backticks, " +
     "no preamble:\n" +
     "{\n" +
     "  \"remove\": [\"<fact(s) from CURRENT FACTS, copied verbatim exactly as shown above, that are " +
     "redundant duplicates and should be removed>\"]\n" +
     "}";

    var requestBody = new
    {
      contents = new[]
      {
      new { role = "user", parts = new[] { new { text = "Identify duplicate/redundant facts to remove." } } }
    },
      systemInstruction = new
      {
        parts = new[] { new { text = systemPrompt } }
      }
    };

    string json = JsonSerializer.Serialize(requestBody);
    var content = new StringContent(json, Encoding.UTF8, "application/json");
    string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent?key={_apiKey}";

    try
    {
      var response = await _http.PostAsync(url, content);
      string responseText = await response.Content.ReadAsStringAsync();

      if (!response.IsSuccessStatusCode)
      {
        Console.WriteLine($"[Dedupe error] Gemini API returned failure: {responseText}");
        return; // fail safe: leave facts untouched
      }

      using var doc = JsonDocument.Parse(responseText);
      var answer = doc.RootElement
          .GetProperty("candidates")[0]
          .GetProperty("content")
          .GetProperty("parts")[0]
          .GetProperty("text")
          .GetString();

      string cleaned = (answer ?? "{}").Trim();
      if (cleaned.StartsWith("```"))
      {
        int firstNewline = cleaned.IndexOf('\n');
        int lastFence = cleaned.LastIndexOf("```");
        if (firstNewline >= 0 && lastFence > firstNewline)
          cleaned = cleaned.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
      }

      using var resultDoc = JsonDocument.Parse(cleaned);
      var root = resultDoc.RootElement;

      var toRemoveText = root.TryGetProperty("remove", out var rm)
          ? rm.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList()
          : new List<string>();

      if (toRemoveText.Count == 0) return;

      var toRemove = deletable
          .Where(e => toRemoveText.Any(r => string.Equals(r, e.Info, StringComparison.OrdinalIgnoreCase)))
          .ToList();

      if (toRemove.Count > 0)
      {
        db.Facts.RemoveRange(toRemove);
        await db.SaveChangesAsync();
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Dedupe error] {ex.Message}");
      // fail safe: leave facts as-is rather than risk wiping on a bad response
    }
  }
}
