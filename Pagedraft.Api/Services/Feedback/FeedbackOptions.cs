namespace Pagedraft.Api.Services.Feedback;

/// <summary>
/// The feedback feature's config surface (Show C2, d1 section (4)). One boolean today, in a
/// feature-named section with an <c>Enabled</c>-style leaf - the same shape as
/// <c>Ai:AnalysisRepair:Enabled</c> and <c>LanguageEngine:LanguageTool:Enabled</c>, rather than a bespoke
/// top-level key.
/// </summary>
public class FeedbackOptions
{
    public const string SectionName = "Feedback";

    /// <summary>
    /// WHETHER THE OWNER'S TRIAGE SURFACE EXISTS ON THIS DEPLOYMENT. Base <c>appsettings.json</c> ships
    /// <c>true</c> (Development inherits it); <c>appsettings.Production.json</c> overrides it to
    /// <c>false</c>, because the triage view reads manuscript-bearing evidence and this app has no
    /// <c>[Authorize]</c> anywhere yet. It flips to <c>true</c> in production the day the Pagewise-style
    /// JWT + Google login lands and the triage routes gain <c>[Authorize]</c>.
    ///
    /// <para>WHAT IT GATES, AND WHAT IT DOES NOT. Gated: <c>GET /api/feedback</c>,
    /// <c>GET /api/feedback/{id}</c> and <c>PATCH /api/feedback/{id}/status</c>. NOT gated:
    /// <c>POST /api/feedback</c> (the widget must keep working when triage is hidden - collecting the
    /// signal is the point) and <c>DELETE /api/feedback/{id}</c> (retract is the voter's own action on
    /// their own row, not a triage operation).</para>
    ///
    /// <para>A gated endpoint returns a PLAIN <c>404</c> when this is false, not a <c>403</c>. This app
    /// already exposes its whole route table through Swagger with nothing in front of it, so a 403 would
    /// leak exactly as much as a 200 and buy nothing; a bodiless 404 is what an unregistered route already
    /// returns, so flag-off and route-absent need no special-casing to look identical.</para>
    ///
    /// <para>THE CLASS DEFAULT IS <c>false</c> ON PURPOSE, and it is not a contradiction of the shipped
    /// base value: it is the safe posture for programmatic and test construction, so an
    /// <c>Options.Create(new FeedbackOptions())</c> in a test that forgot to think about the flag gets
    /// the closed surface rather than the open one.</para>
    /// </summary>
    public bool TriageEnabled { get; set; }
}
