using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Mail;
using System.Web;
using Microsoft.EntityFrameworkCore;

namespace DeliAi.Backend;

public class DeliBot
{
  private readonly IDbContextFactory<DeliDbContext> _dbContextFactory;

  private readonly HttpClient _http = new();
  private readonly string _apiKey;

  private readonly string _smtpHost;
  private readonly int _smtpPort;
  private readonly string _smtpUser;
  private readonly string _smtpPass;
  private readonly string _alertEmailTo;

  private const string DefaultRefusalRule =
     "If you do not know the answer to a question with full certainty, or the answer is not explicitly stated in these facts, you must respond only with: Sorry, I do not have that information. If this is a mistake, add the information using the 'manage info' button.";

  public DeliBot(IDbContextFactory<DeliDbContext> dbContextFactory)
  {
    _dbContextFactory = dbContextFactory;

    _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
        ?? throw new Exception("GEMINI_API_KEY environment variable not set.");

    _smtpHost = Environment.GetEnvironmentVariable("SMTP_HOST")
        ?? throw new Exception("SMTP_HOST environment variable not set.");
    _smtpPort = int.Parse(Environment.GetEnvironmentVariable("SMTP_PORT")
        ?? throw new Exception("SMTP_PORT environment variable not set."));
    _smtpUser = Environment.GetEnvironmentVariable("SMTP_USER")
        ?? throw new Exception("SMTP_USER environment variable not set.");
    _smtpPass = Environment.GetEnvironmentVariable("SMTP_PASS")
        ?? throw new Exception("SMTP_PASS environment variable not set.");
    _alertEmailTo = Environment.GetEnvironmentVariable("ALERT_EMAIL_TO")
        ?? throw new Exception("ALERT_EMAIL_TO environment variable not set.");
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

  private async Task SendFlagAlertEmailAsync(string msg, string reason)
  {
    try
    {
      TimeZoneInfo cstZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
      using var message = new MailMessage();
      message.From = new MailAddress(_smtpUser);
      message.To.Add(_alertEmailTo);
      message.Subject = "Deli Assistant: message flagged";
      message.IsBodyHtml = true;

      string safeMsg = HttpUtility.HtmlEncode(msg);
      string safeReason = HttpUtility.HtmlEncode(reason);
      string centralTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cstZone).ToString("h:mmtt dddd M-d-yy");
      string utcTime = DateTime.UtcNow.ToString("o");

      message.Body = $"""
        <html>
          <body style="margin:0; padding:0; background-color:#f2f2f2; font-family: Arial, Helvetica, sans-serif;">
            <table role="presentation" width="100%" style="border-collapse:collapse; padding:24px 0;">
              <tr>
                <td align="center">
                  <table role="presentation" width="480" style="background:#ffffff; border-radius:8px; overflow:hidden; border:1px solid #e2e2e2;">

                    <tr>
                      <td style="background:#c0392b; padding:16px 24px;">
                        <span style="color:#ffffff; font-size:18px; font-weight:bold;">⚠️ Message Flagged</span>
                      </td>
                    </tr>

                    <tr>
                      <td style="padding:20px 24px 4px 24px; color:#333333; font-size:14px;">
                        A message was flagged at <strong>{centralTime}</strong> (Central).
                      </td>
                    </tr>

                    <tr>
                      <td style="padding:16px 24px;">
                        <table role="presentation" width="100%" style="border-collapse:collapse; font-size:14px;">
                          <tr>
                            <td style="padding:8px 0; color:#888888; width:90px; vertical-align:top;">Message</td>
                            <td style="padding:8px 0; color:#111111;">{safeMsg}</td>
                          </tr>
                          <tr style="border-top:1px solid #eeeeee;">
                            <td style="padding:8px 0; color:#888888; vertical-align:top;">Reason</td>
                            <td style="padding:8px 0; color:#111111;">{safeReason}</td>
                          </tr>
                          <tr style="border-top:1px solid #eeeeee;">
                            <td style="padding:8px 0; color:#888888; vertical-align:top;">Time (UTC)</td>
                            <td style="padding:8px 0; color:#111111;">{utcTime}</td>
                          </tr>
                        </table>
                      </td>
                    </tr>

                    <tr>
                      <td style="padding:14px 24px; background:#fafafa; border-top:1px solid #eeeeee;">
                        <span style="color:#999999; font-size:12px;">This is an automated alert from the Deli Assistant.</span>
                      </td>
                    </tr>

                  </table>
                </td>
              </tr>
            </table>
          </body>
        </html>
        """;

      using var client = new SmtpClient(_smtpHost, _smtpPort)
      {
        Credentials = new NetworkCredential(_smtpUser, _smtpPass),
        EnableSsl = true
      };

      await client.SendMailAsync(message);
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Email alert error] Could not send flagged message email: {ex.Message}");
    }
  }

  private async Task PruneUnansweredQuestionsAsync()
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();

    var unanswered = await db.UnansweredQuestions.OrderBy(u => u.Id).ToListAsync();
    if (unanswered.Count == 0) return;

    var facts = await db.Facts.Where(f => f.Info != DefaultRefusalRule).ToListAsync();

    string questionList = string.Join("\n", unanswered.Select(e => $"- {e.Info}"));
    string factList = string.Join("\n", facts.Select(e => $"- {e.Info}"));

    string systemPrompt =
        "You are checking a log of customer questions that a deli assistant bot could NOT answer at " +
        "the time they were asked, because no matching fact existed in its knowledge base at that time.\n\n" +
        $"CURRENT KNOWN FACTS:\n{factList}\n\n" +
        $"PREVIOUSLY UNANSWERED QUESTIONS:\n{questionList}\n\n" +
        "Check each unanswered question against the CURRENT KNOWN FACTS list. If a fact now covers that " +
        "question's topic (even if worded differently), that question is now answered and should be " +
        "REMOVED from consideration. Otherwise keep it.\n\n" +
        "Respond with ONLY a JSON object shaped exactly like this, nothing else — no markdown, no " +
        "backticks, no preamble:\n" +
        "{\n" +
        "  \"stillUnanswered\": [\"<questions from the input list that are still NOT answered, copied verbatim>\"]\n" +
        "}";

    var requestBody = new
    {
      contents = new[]
      {
        new { role = "user", parts = new[] { new { text = "Check the unanswered questions against current facts." } } }
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
        Console.WriteLine($"[Prune error] Gemini API returned failure: {responseText}");
        return; // fail safe: leave the unanswered log untouched
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

      var stillUnanswered = root.TryGetProperty("stillUnanswered", out var su)
          ? su.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList()
          : null;

      if (stillUnanswered == null) return; // couldn't parse, don't wipe the log

      var toRemove = unanswered.Where(e => !stillUnanswered.Contains(e.Info)).ToList();
      if (toRemove.Count > 0)
      {
        db.UnansweredQuestions.RemoveRange(toRemove);
        await db.SaveChangesAsync();
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Prune error] {ex.Message}");
      // fail safe: leave the unanswered log as-is rather than risk wiping it on a bad response
    }
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
      await SendFlagAlertEmailAsync(question, flagReason);
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

  public async Task<List<object>> GetAllFactsAsync()
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();
    var facts = await db.Facts.Where(e => e.Info != DefaultRefusalRule).OrderBy(e => e.Id).ToListAsync();
    return facts
        .Select((e, i) => new { index = i, info = e.Info })
        .Cast<object>()
        .ToList();
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

  public async Task<string> AddAnsweredSuggestionAsync(string suggestion, string rawAnswer)
  {
    string systemPrompt =
        "You are helping turn a short customer-service answer into a complete, standalone fact for a " +
        "deli assistant bot's knowledge base.\n\n" +
        "You will be given:\n" +
        "- SUGGESTION: a prompt describing what info was missing (e.g. \"Add whether the deli carries " +
        "gluten-free bread.\")\n" +
        "- RAW ANSWER: what the employee typed in response, which may be short, informal, or missing " +
        "context (e.g. \"yes we do\", \"9pm\", \"back cooler\").\n\n" +
        "Your job: combine the SUGGESTION and RAW ANSWER into ONE complete, standalone sentence (or short " +
        "set of sentences) that fully states the fact, with no missing context. Someone reading ONLY this " +
        "output — without ever seeing the suggestion — must understand exactly what it means.\n\n" +
        "RULES:\n" +
        "1. Pull the specific subject/item out of the SUGGESTION and restate it explicitly in the fact. " +
        "Never leave a fact that only makes sense next to the suggestion (e.g. never just output \"Yes we do\" " +
        "or \"9pm\" on their own).\n" +
        "2. Do not add any information that isn't implied by the SUGGESTION or stated in the RAW ANSWER. " +
        "Do not guess prices, hours, or specifics that weren't given.\n" +
        "3. If the RAW ANSWER already is a complete, standalone statement that includes the subject, keep " +
        "it close to as-is (light grammar cleanup only, no content changes).\n" +
        "4. If the RAW ANSWER is ambiguous, confusing, or doesn't seem to actually answer the SUGGESTION, " +
        "return your best faithful merge anyway — do not refuse, do not add caveats or commentary.\n" +
        "5. Keep it concise, plain, everyday wording — no fancy phrasing, no fluff.\n" +
        "6. Respond with ONLY the final fact text, nothing else — no quotes, no labels, no markdown, no " +
        "preamble, no explanation.";

    var requestBody = new
    {
      contents = new[]
      {
      new {
        role = "user",
        parts = new[] {
          new {
            text = $"SUGGESTION:\n{suggestion}\n\nRAW ANSWER:\n{rawAnswer}\n\nReturn the merged fact only."
          }
        }
      }
    },
      systemInstruction = new
      {
        parts = new[] { new { text = systemPrompt } }
      }
    };

    string json = JsonSerializer.Serialize(requestBody);
    var content = new StringContent(json, Encoding.UTF8, "application/json");
    string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent?key={_apiKey}";

    string finalFact = rawAnswer; // fallback if anything goes wrong

    try
    {
      var response = await _http.PostAsync(url, content);
      string responseText = await response.Content.ReadAsStringAsync();

      if (response.IsSuccessStatusCode)
      {
        using var doc = JsonDocument.Parse(responseText);
        var answer = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        if (!string.IsNullOrWhiteSpace(answer))
          finalFact = answer.Trim();
      }
      else
      {
        Console.WriteLine($"[Merge answer error] Gemini API returned failure: {responseText}");
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Merge answer error] {ex.Message}");
    }

    // Save the merged (or fallback raw) fact using the existing add path
    return await AddFactAsync(finalFact);
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

  public async Task<List<string>> getSuggestions()
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();

    var unanswered = await db.UnansweredQuestions.OrderBy(e => e.Id).ToListAsync();

    if (unanswered.Count == 0)
    {
      return new List<string> { "No unanswered questions yet — nothing to suggest." };
    }

    var facts = await db.Facts.Where(e => e.Info != DefaultRefusalRule).ToListAsync();

    string questionList = string.Join("\n", unanswered.Select(e => $"- {e.Info}"));
    string factList = string.Join("\n", facts.Select(e => $"- {e.Info}"));

    string systemPrompt =
        "You are analyzing a log of customer questions that a deli assistant bot could NOT answer at " +
        "the time they were asked, because no matching fact existed in its knowledge base at that time.\n\n" +
        $"CURRENT KNOWN FACTS:\n{factList}\n\n" +
        $"PREVIOUSLY UNANSWERED QUESTIONS:\n{questionList}\n\n" +
        "STEP 1: Some of these questions may have since been answered — check each unanswered question " +
        "against the CURRENT KNOWN FACTS list. If a fact now covers that question's topic (even if worded " +
        "differently), that question is now answered and should be REMOVED from consideration entirely.\n\n" +
        "STEP 2: Group the REMAINING (still-unanswered) questions by underlying topic/intent, not by exact " +
        "wording. Two questions belong in the same group if they are asking about the same general thing, " +
        "even if phrased completely differently (e.g. \"where is the coleslaw\" and \"bags of slaw are " +
        "where\" are the SAME topic). Find the 3 topics with the most questions grouped under them, AS LONG AS THERE ARE AT LEAST 2 ENTRIES FOR THAT TOPIC. If " +
        "there is a tie for 3rd place, pick whichever tied topic seems most useful for a deli to add.\n\n" +
        "For each of the 3 topics, write ONE specific, concrete suggestion that names the actual thing to " +
        "add — reuse the specific noun/subject from the original question itself, don't generalize it into " +
        "a broader category. For example, if the question was \"who is the store manager\", the suggestion " +
        "must be \"Add the name of the store manager.\" — NOT a vague broader thing like \"Add contact info " +
        "for store management.\" Stay as close to the original question's specific subject as possible.\n\n" +
        "Keep suggestions simple and concise, plain everyday wording, no fancy phrasing, but don't drop " +
        "important specific details from the original question.\n\n" +
        "If there are fewer than 3 distinct still-unanswered topics total, return fewer than 3 suggestions " +
        "rather than inventing filler ones.\n\n" +
        "Respond with ONLY a JSON object shaped exactly like this, nothing else — no markdown, no backticks, " +
        "no preamble:\n" +
        "{\n" +
        "  \"stillUnanswered\": [\"<questions from the input list that were NOT removed in step 1, copied verbatim>\"],\n" +
        "  \"suggestions\": [\"<up to 3 specific suggestion strings from step 2>\"]\n" +
        "}";

    var requestBody = new
    {
      contents = new[]
      {
      new { role = "user", parts = new[] { new { text = "Analyze the unanswered questions and return suggestions." } } }
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
      return new List<string> { "Sorry, something went wrong generating suggestions." };
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

    try
    {
      using var resultDoc = JsonDocument.Parse(cleaned);
      var root = resultDoc.RootElement;

      var stillUnanswered = root.TryGetProperty("stillUnanswered", out var su)
          ? su.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList()
          : new List<string>();

      var suggestions = root.TryGetProperty("suggestions", out var sg)
          ? sg.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").Take(3).ToList()
          : new List<string>();

      // Prune the log: only keep questions the model says are still unanswered
      var toRemove = unanswered.Where(e => !stillUnanswered.Contains(e.Info)).ToList();
      if (toRemove.Count > 0)
      {
        db.UnansweredQuestions.RemoveRange(toRemove);
        await db.SaveChangesAsync();
      }

      return suggestions.Count > 0
          ? suggestions
          : new List<string> { "No suggestions right now — recent questions may already be answered." };
    }
    catch (JsonException ex)
    {
      Console.WriteLine($"[Suggestions parse error] Could not parse model response as JSON: {ex.Message}");
      Console.WriteLine($"[Suggestions raw response] {cleaned}");
      return new List<string> { "Couldn't generate suggestions right now — please try again." };
    }
  }

  public async Task<List<string>> removeSuggestion(string suggestion)
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();

    var unanswered = await db.UnansweredQuestions.OrderBy(e => e.Id).ToListAsync();
    var facts = await db.Facts.Where(e => e.Info != DefaultRefusalRule).ToListAsync();

    string questionList = string.Join("\n", unanswered.Select(e => $"- {e.Info}"));
    string factList = string.Join("\n", facts.Select(e => $"- {e.Info}"));

    string systemPrompt =
      "You are helping maintain a deli assistant bot's knowledge base.\n\n" +
      "The user has chosen to REMOVE a suggestion that the bot previously generated.\n" +
      "A suggestion corresponds to one or more unanswered customer questions.\n\n" +
      "Your job is to determine EXACTLY which unanswered questions should be removed — and to be " +
      "conservative. It is much worse to remove a question that doesn't belong than to leave one behind.\n\n" +
      "You will be given:\n" +
      "- The suggestion the user wants removed\n" +
      "- The full list of CURRENT KNOWN FACTS\n" +
      "- The full list of PREVIOUSLY UNANSWERED QUESTIONS\n\n" +
      "RULES:\n" +
      "1. A question relates to the suggestion ONLY if it is asking about the exact same specific " +
      "item, product, or topic named in the suggestion — not merely a similar, related, or same-category " +
      "item.\n\n" +
      "2. Treat different specific items as DIFFERENT topics even if they share a word or category. " +
      "For example: \"beans\" and \"refried beans\" are DIFFERENT items — do not match one for the other. " +
      "\"turkey\" and \"turkey club sandwich\" are DIFFERENT. \"cheese\" and \"cheddar cheese\" are " +
      "DIFFERENT unless the question is genuinely generic and non-specific. When in doubt about whether " +
      "two things are the same specific item, treat them as DIFFERENT and do not remove.\n\n" +
      "3. A question only counts as matching if a reasonable person would say the suggestion was " +
      "generated directly and specifically because of that question — not because they're topically " +
      "adjacent or in the same food category.\n\n" +
      "4. Remove ALL (and only) questions that pass rules 1–3. Do not remove anything else.\n\n" +
      "5. Respond ONLY with JSON in this exact shape, nothing else — no markdown, no backticks, no " +
      "preamble:\n" +
      "{\n" +
      "  \"remove\": [\"<list of unanswered questions to delete, copied verbatim>\"]\n" +
      "}\n";

    var requestBody = new
    {
      contents = new[]
        {
            new {
                role = "user",
                parts = new[] {
                    new {
                        text =
                            $"SUGGESTION TO REMOVE:\n{suggestion}\n\n" +
                            $"CURRENT KNOWN FACTS:\n{factList}\n\n" +
                            $"UNANSWERED QUESTIONS:\n{questionList}\n\n" +
                            "Return JSON only."
                    }
                }
            }
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
      return await getSuggestions();
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

    try
    {
      using var resultDoc = JsonDocument.Parse(cleaned);
      var root = resultDoc.RootElement;

      var toRemoveText = root.TryGetProperty("remove", out var rm)
          ? rm.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList()
          : new List<string>();

      if (toRemoveText.Count > 0)
      {
        var toRemove = unanswered.Where(q => toRemoveText.Contains(q.Info)).ToList();
        if (toRemove.Count > 0)
        {
          db.UnansweredQuestions.RemoveRange(toRemove);
          await db.SaveChangesAsync();
        }
      }

      // Return updated suggestions
      return await getSuggestions();
    }
    catch (JsonException ex)
    {
      Console.WriteLine($"[RemoveSuggestion parse error] {ex.Message}");
      Console.WriteLine($"[RemoveSuggestion raw response] {cleaned}");
      return await getSuggestions();
    }
  }

  public async Task<bool> LogWrongInfoReport(string response, string name, bool fullyWrong, string howToImprove)
  {
    try
    {
      await using var db = await _dbContextFactory.CreateDbContextAsync();
      db.WrongInfoReports.Add(new WrongInfoReportEntity
      {
        Response = response,
        Name = name,
        FullyWrong = fullyWrong,
        HowToImprove = howToImprove
      });
      await db.SaveChangesAsync();
      return true;
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Wrong info log error] Could not save wrong-info report: {ex.Message}");
      return false;
    }
  }

  public async Task<List<object>> GetWrongInfoReports()
  {
    try
    {
      await using var db = await _dbContextFactory.CreateDbContextAsync();
      var reports = await db.WrongInfoReports.OrderBy(r => r.Id).ToListAsync();

      return reports.Select(r => new
      {
        response = r.Response,
        fullyWrong = r.FullyWrong,
        howToImprove = r.HowToImprove
      }).Cast<object>().ToList();
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Wrong info log error] Could not read wrong-info reports: {ex.Message}");
      return new List<object>();
    }
  }

  public async Task<bool> ResolveWrongInfo(string assistantResponse, string correctedInfo)
  {
    try
    {
      await using var db = await _dbContextFactory.CreateDbContextAsync();

      var report = await db.WrongInfoReports.FirstOrDefaultAsync(r => r.Response == assistantResponse);
      if (report == null)
      {
        Console.WriteLine("[Wrong info resolve error] Could not find matching report.");
        return false;
      }

      db.WrongInfoReports.Remove(report);
      await db.SaveChangesAsync();

      // Ask AI which stored fact(s) this bad response likely came from, and remove them
      var sourceFacts = await FindFactsBehindResponseAsync(assistantResponse);

      if (sourceFacts.Count > 0)
      {
        var toRemove = await db.Facts
            .Where(e => e.Info != DefaultRefusalRule && sourceFacts.Contains(e.Info))
            .ToListAsync();
        if (toRemove.Count > 0)
        {
          db.Facts.RemoveRange(toRemove);
          await db.SaveChangesAsync();
        }
      }

      // Add the corrected fact
      await AddFactAsync(correctedInfo);
      await DedupeFactsAsync();

      return true;
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Wrong info resolve error] {ex.Message}");
      return false;
    }
  }

  private async Task<List<string>> FindFactsBehindResponseAsync(string assistantResponse)
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync();
    var facts = await db.Facts.Where(e => e.Info != DefaultRefusalRule).ToListAsync();

    string factList = string.Join("\n", facts.Select(e => $"- {e.Info}"));

    string systemPrompt =
        "You are helping maintain a deli assistant bot's knowledge base.\n\n" +
        "The bot previously gave a customer the ASSISTANT RESPONSE below. That response was generated by " +
        "the bot picking one or more facts from its knowledge base and repeating/lightly rephrasing them. " +
        "A user has reported that response as wrong or incomplete, and it needs to be corrected.\n\n" +
        "Your job: identify EXACTLY which fact(s) in the CURRENT KNOWN FACTS list the bot most likely used " +
        "to generate that response — the source fact(s) that need to be fixed or removed. Be conservative: " +
        "only include a fact if it's clearly and specifically the source of that response, not merely " +
        "related or topically similar.\n\n" +
        $"CURRENT KNOWN FACTS:\n{factList}\n\n" +
        $"ASSISTANT RESPONSE:\n{assistantResponse}\n\n" +
        "Respond with ONLY a JSON object shaped exactly like this, nothing else — no markdown, no " +
        "backticks, no preamble:\n" +
        "{\n" +
        "  \"sourceFacts\": [\"<fact(s) from CURRENT KNOWN FACTS, copied verbatim, that are the source of " +
        "the response>\"]\n" +
        "}";

    var requestBody = new
    {
      contents = new[]
      {
      new { role = "user", parts = new[] { new { text = "Identify the source fact(s)." } } }
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
        Console.WriteLine($"[Find source fact error] Gemini API returned failure: {responseText}");
        return new List<string>();
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

      return root.TryGetProperty("sourceFacts", out var sf)
          ? sf.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList()
          : new List<string>();
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Find source fact error] {ex.Message}");
      return new List<string>();
    }
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

  public async Task<bool> DismissWrongInfoReport(string response)
  {
    try
    {
      await using var db = await _dbContextFactory.CreateDbContextAsync();
      var report = await db.WrongInfoReports.FirstOrDefaultAsync(r => r.Response == response);

      if (report == null)
      {
        Console.WriteLine("[Wrong info dismiss error] Could not find matching report.");
        return false;
      }

      db.WrongInfoReports.Remove(report);
      await db.SaveChangesAsync();
      return true;
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Wrong info dismiss error] {ex.Message}");
      return false;
    }
  }
}