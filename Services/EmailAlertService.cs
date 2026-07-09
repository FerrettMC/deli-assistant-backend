// using System.Text;
// using System.Text.Json;
// using System.Web;

// namespace DeliAi.Backend.Services;

// public class EmailAlertService
// {
//   private readonly HttpClient _http = new();
//   private readonly string _resendApiKey;
//   private readonly string _alertEmailTo;

//   public EmailAlertService()
//   {
//     _resendApiKey = Environment.GetEnvironmentVariable("RESEND_API_KEY")
//         ?? throw new Exception("RESEND_API_KEY environment variable not set.");
//     _alertEmailTo = Environment.GetEnvironmentVariable("ALERT_EMAIL_TO")
//         ?? throw new Exception("ALERT_EMAIL_TO environment variable not set.");
//   }

//   public async Task SendFlagAlertEmailAsync(string msg, string reason)
//   {
//     try
//     {
//       TimeZoneInfo cstZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
//       string emailSubject = "Deli Assistant: message flagged";

//       string safeMsg = HttpUtility.HtmlEncode(msg);
//       string safeReason = HttpUtility.HtmlEncode(reason);
//       string centralTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cstZone).ToString("h:mmtt dddd M-d-yy");
//       string utcTime = DateTime.UtcNow.ToString("o");

//       string emailBody = $"""
//         <html>
//           <body style="margin:0; padding:0; background-color:#f2f2f2; font-family: Arial, Helvetica, sans-serif;">
//             <table role="presentation" width="100%" style="border-collapse:collapse; padding:24px 0;">
//               <tr>
//                 <td align="center">
//                   <table role="presentation" width="480" style="background:#ffffff; border-radius:8px; overflow:hidden; border:1px solid #e2e2e2;">

//                     <tr>
//                       <td style="background:#c0392b; padding:16px 24px;">
//                         <span style="color:#ffffff; font-size:18px; font-weight:bold;">⚠️ Message Flagged</span>
//                       </td>
//                     </tr>

//                     <tr>
//                       <td style="padding:20px 24px 4px 24px; color:#333333; font-size:14px;">
//                         A message was flagged at <strong>{centralTime}</strong> (Central).
//                       </td>
//                     </tr>

//                     <tr>
//                       <td style="padding:16px 24px;">
//                         <table role="presentation" width="100%" style="border-collapse:collapse; font-size:14px;">
//                           <tr>
//                             <td style="padding:8px 0; color:#888888; width:90px; vertical-align:top;">Message</td>
//                             <td style="padding:8px 0; color:#111111;">{safeMsg}</td>
//                           </tr>
//                           <tr style="border-top:1px solid #eeeeee;">
//                             <td style="padding:8px 0; color:#888888; vertical-align:top;">Reason</td>
//                             <td style="padding:8px 0; color:#111111;">{safeReason}</td>
//                           </tr>
//                           <tr style="border-top:1px solid #eeeeee;">
//                             <td style="padding:8px 0; color:#888888; vertical-align:top;">Time (UTC)</td>
//                             <td style="padding:8px 0; color:#111111;">{utcTime}</td>
//                           </tr>
//                         </table>
//                       </td>
//                     </tr>

//                     <tr>
//                       <td style="padding:14px 24px; background:#fafafa; border-top:1px solid #eeeeee;">
//                         <span style="color:#999999; font-size:12px;">This is an automated alert from the Deli Assistant.</span>
//                       </td>
//                     </tr>

//                   </table>
//                 </td>
//               </tr>
//             </table>
//           </body>
//         </html>
//         """;

//       var resendBody = new
//       {
//         from = "Deli Assistant <onboarding@resend.dev>",
//         to = new[] { _alertEmailTo },
//         subject = emailSubject,
//         html = emailBody
//       };

//       var resendContent = new StringContent(
//           JsonSerializer.Serialize(resendBody), Encoding.UTF8, "application/json");

//       using var resendRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
//       {
//         Content = resendContent
//       };
//       resendRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _resendApiKey);

//       var resendResponse = await _http.SendAsync(resendRequest);
//       if (!resendResponse.IsSuccessStatusCode)
//       {
//         string errorBody = await resendResponse.Content.ReadAsStringAsync();
//         Console.WriteLine($"[Email alert error] Resend API returned failure: {errorBody}");
//       }
//     }
//     catch (Exception ex)
//     {
//       Console.WriteLine($"[Email alert error] Could not send flagged message email: {ex.Message}");
//     }
//   }
// }
