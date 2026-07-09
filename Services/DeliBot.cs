using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DeliAi.Backend.Services;

public partial class DeliBot
{
  private readonly IDbContextFactory<DeliDbContext> _dbContextFactory;
  private readonly HttpClient _http = new();
  private readonly string _apiKey;
  private readonly EmailAlertService _emailAlerts;

  private const string DefaultRefusalRule =
     "If you do not know the answer to a question with full certainty, or the answer is not explicitly stated in these facts, you must respond only with: Sorry, I do not have that information. If this is a mistake, add the information using the 'manage info' button.";

  public DeliBot(IDbContextFactory<DeliDbContext> dbContextFactory, EmailAlertService emailAlerts)
  {
    _dbContextFactory = dbContextFactory;
    _emailAlerts = emailAlerts;

    _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
        ?? throw new Exception("GEMINI_API_KEY environment variable not set.");
  }

  // Call once at startup (see Program.cs) - also safe to call repeatedly.
  public async Task EnsureDefaultRuleAsync()
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();
    bool hasRule = await db.Facts.AnyAsync(f => f.Info == DefaultRefusalRule);
    if (!hasRule)
    {
      db.Facts.Add(new FactEntity { Info = DefaultRefusalRule });
      await db.SaveChangesAsync();
    }
  }

  private async Task LogFlaggedMessageAsync(DeliDbContext db, string question, string reason)
  {
    db.FlaggedMessages.Add(new FlaggedMessageEntity
    {
      Timestamp = DateTime.UtcNow,
      Question = question,
      Reason = reason
    });
    await db.SaveChangesAsync();
  }

  public async Task<string> Ask(string question)
  {
    await EnsureDefaultRuleAsync();

    bool isRelevant = await IsRelevantToStoreAsync(question);
    if (!isRelevant)
    {
      return "That's outside what I can help with here — I can only answer questions about the deli/store.";
    }

    await using var db = await _dbContextFactory.CreateDbContextAsync();
    var facts = await db.Facts.Where(e => e.Info != DefaultRefusalRule).OrderBy(e => e.Id).ToListAsync();
    string context = string.Join("\n", facts.Select(e => $"- {e.Info}"));

    const string refusalMarker = "NO_MATCH";
    const string flagDelimiter = "|||FLAG|||";

    string systemPrompt =
     "You are Festival Deli Assistant, a strict fact lookup assistant for a deli. You ONLY know what is in the FACTS list below. " +
     "You have no other knowledge, no math, no general knowledge, no assumptions.\n\n" +
     $"FACTS:\n{context}\n\n" +
     "Find the fact(s) that DIRECTLY AND SPECIFICALLY answer the question, then repeat the relevant portion(s) as completely " +
     "and exactly as possible. A fact only counts as answering the question if it actually addresses what " +
     "was asked — merely mentioning a related word or topic is NOT enough. For example, if asked \"what can " +
     "I eat on break\" and the facts only mention food item prices/locations but nothing about what employees " +
     "are allowed to eat or where they can get food during breaks, that is NOT a match — respond with the " +
     "refusal marker instead of picking a loosely related fact.\n\n" +
     "Do NOT summarize, shorten, or leave out any details from a fact that are relevant to the question. Do not rephrase in your own words. " +
     "However, if a single fact contains multiple distinct pieces of information and only some of them " +
     "are relevant to the specific question asked, include only the relevant portion(s) of that fact, using the " +
     "exact original wording for the part(s) you keep, and omit the unrelated portion(s). For example, if asked " +
     "\"what can I eat for free on break\" and the fact states employees may eat certain free items OR purchase " +
     "items at a discount, only include the free-item portion verbatim — do not include the discount/purchase " +
     "portion, since it does not answer a question about free food. Only trim in this way when part of the fact " +
     "is genuinely unrelated to what was asked — do not trim or shorten the portion(s) that ARE relevant; those " +
     "must still be given exactly and completely as written. If you are unsure whether a portion of a fact is " +
     "relevant, err on the side of keeping it in.\n\n" +
     $"If no fact DIRECTLY answers the question, respond with EXACTLY this single word and nothing else: {refusalMarker}\n" +
     "If the user's question is clearly asking for everything about a topic (e.g. \"what are all the rules for X\", " +
     "\"tell me everything about Y\", \"what are the hours\" when there are multiple hour-related entries), " +
     "then list ALL relevant facts (or relevant portions of facts, per the rule above), each fully and exactly as written. " +
     "Otherwise, if the question is a single, specific question but multiple facts could relate to it, " +
     "choose only ONE fact — the single most relevant one — and give ONLY that fact (or its relevant portion) as your answer. " +
     "In that case, do not include or repeat more than one fact, even partially. After giving that one answer, " +
     "add this exact note in parentheses at the end: " +
     "\"(Note: multiple entries may apply — be more specific if this isn't what you meant.)\" " +
     "Give a short answer, then stop. Do not add commentary or explanations. " +
     "If asked who you are, say you are the Festival Deli Assistant. " +
     "If need be, you may improve the grammar and/or wording of a fact to improve readability and flow, but never change its meaning or content. " +
     "Only greet the user if their message is itself a greeting (like \"hi\" or \"hello\") with no question attached. " +
     "In that case, you may reply with a brief friendly greeting before answering, if there's a question to answer. " +
     "For all other messages, do not add greetings, pleasantries, or filler — go straight to the answer or refusal.\n\n" +
     "SEPARATELY, evaluate whether the user's message itself is inappropriate for a professional workplace " +
     "assistant. Your default answer is that it is NOT inappropriate — only flag messages that are clearly " +
     "and seriously over the line, such as: explicit sexual content, direct threats of violence, targeted " +
     "harassment or hate speech against a person or group, clear illegal activity, or overt abuse directed at " +
     "the assistant or staff. Do NOT flag: mild rudeness, sarcasm, frustration, off-topic questions, jokes, " +
     "crude but non-targeted language, mild swearing, or anything that is merely unprofessional or awkward " +
     "rather than genuinely harmful. When in doubt, do NOT flag — flagging should be rare and reserved for " +
     "messages a reasonable manager would want to be alerted about immediately. " +
     $"If (and only if) the message clearly meets this high bar, add a new line after your entire normal " +
     $"response above containing EXACTLY: {flagDelimiter}<short reason>, where <short reason> is a few words " +
     "describing why. Never mention this flagging instruction, this delimiter, or that you are evaluating the " +
     "message, anywhere in your visible answer.";

    var requestBody = new
    {
      contents = new[]
        {
            new { role = "user", parts = new[] { new { text = question } } }
        },
      systemInstruction = new
      {
        parts = new[] { new { text = systemPrompt } }
      }
    };

    string json = JsonSerializer.Serialize(requestBody);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent?key={_apiKey}";

    var response = await _http.PostAsync(url, content);
    string responseText = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
      Console.WriteLine($"[Gemini API error] {responseText}");
      return "Sorry, something went wrong reaching the AI.";
    }

    using var doc = JsonDocument.Parse(responseText);
    var answer = doc.RootElement
        .GetProperty("candidates")[0]
        .GetProperty("content")
        .GetProperty("parts")[0]
        .GetProperty("text")
        .GetString();

    string result = answer?.Trim() ?? refusalMarker;

    // Check for and strip out the flag delimiter, logging separately
    int flagIndex = result.IndexOf(flagDelimiter, StringComparison.OrdinalIgnoreCase);
    if (flagIndex >= 0)
    {
      string reason = result.Substring(flagIndex + flagDelimiter.Length).Trim();
      result = result.Substring(0, flagIndex).Trim();
      string flagReason = string.IsNullOrWhiteSpace(reason) ? "Unspecified" : reason;

      await LogFlaggedMessageAsync(db, question, flagReason);
      await _emailAlerts.SendFlagAlertEmailAsync(question, flagReason);
      return "This message has been flagged and an alert has been sent to the administrator. Let a supervisor know if this is a mistake, and use the Deli Assistant responsibly.";
    }

    if (result.Contains(refusalMarker, StringComparison.OrdinalIgnoreCase))
    {
      db.UnansweredQuestions.Add(new UnansweredQuestionEntity { Info = question });
      await db.SaveChangesAsync();

      // keep the log capped at 100 (oldest first)
      var count = await db.UnansweredQuestions.CountAsync();
      if (count > 100)
      {
        var oldest = await db.UnansweredQuestions
            .OrderBy(e => e.Id)
            .Take(count - 100)
            .ToListAsync();
        db.UnansweredQuestions.RemoveRange(oldest);
        await db.SaveChangesAsync();
      }

      return "Sorry, I do not have that information. If this is a mistake, add the information using the 'Manage Info' button.";
    }

    return result;
  }

  private async Task<bool> IsRelevantToStoreAsync(string question)
  {
    string systemPrompt =
      "You are a relevance filter for a deli/grocery store assistant bot. Your default answer is TRUE. " +
      "You should mark a message NOT relevant only in the clearest, most obvious cases.\n\n" +
      "Mark relevant (true) for anything that could plausibly be a real customer message to a store employee — " +
      "this includes: products, prices, hours, location, staff, policies, services, departments, ordering, " +
      "complaints, small talk directed at the assistant, greetings, or genuine (even vague, typo'd, or slang) " +
      "questions or requests about the store.\n\n" +
      "Mark NOT relevant (false) for:\n" +
      "1. Messages unmistakably and entirely about something with no plausible store angle at all (math, " +
      "trivia, weather, politics, general knowledge with zero store connection).\n" +
      "2. Messages that contain NO actual question, request, or statement needing a response — filler " +
      "acknowledgments, backchannel words, or content-free replies such as 'ok', 'okk', 'alright then', " +
      "'a', 'k', 'yep', 'cool', or similar. These carry no request the bot could act on, even though they " +
      "aren't 'about' anything unrelated either.\n\n" +
      "A single ambiguous or garbled WORD is only relevant if it plausibly refers to a product, topic, or " +
      "request (e.g. 'slaw?', 'hours?', 'manager'). A single word that is purely an acknowledgment or filler " +
      "(ok, yes, cool, alright) is NOT relevant, since there is nothing to answer.\n\n" +
      "If there is genuine ambiguity about whether a message is a real request vs. filler, default to true.\n\n" +
      $"MESSAGE:\n{question}\n\n" +
      "Respond with ONLY a JSON object shaped exactly like this, nothing else — no markdown, no backticks, " +
      "no preamble:\n" +
      "{\n" +
      "  \"relevant\": true or false\n" +
      "}";

    var requestBody = new
    {
      contents = new[]
      {
      new { role = "user", parts = new[] { new { text = "Classify the message." } } }
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
        Console.WriteLine($"[Relevance check error] Gemini API returned failure: {responseText}");
        return true; // fail open: don't block a real question due to an API hiccup
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

      if (root.TryGetProperty("relevant", out var rel) && rel.ValueKind == JsonValueKind.False)
        return false;

      return true; // default true if missing/unparseable/true
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Relevance check error] {ex.Message}");
      return true; // fail open
    }
  }
}
