namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// The product-chat feature's config surface. One boolean today, in a feature-named section with an
/// <c>Enabled</c>-style leaf - the same shape as <c>Feedback:TriageEnabled</c>,
/// <c>Ai:AnalysisRepair:Enabled</c> and <c>LanguageEngine:LanguageTool:Enabled</c>, rather than a
/// bespoke top-level key.
/// </summary>
public class ProductChatOptions
{
    public const string SectionName = "ProductChat";

    /// <summary>
    /// WHETHER <see cref="ProductChatRouter"/>'s answer is USED (g1). SHIPPED <c>true</c> SINCE g2, which
    /// wrote the per-route prompt blocks; g3 measures them on the owner's real manuscript, and until it
    /// passes nothing here may be described as verified.
    ///
    /// <para>WHAT FALSE MEANS, PRECISELY: the route is still RESOLVED and LOGGED on every turn, because a
    /// route nobody can see is a route nobody can calibrate, and <c>ProductChatRouter</c> is pure so
    /// resolving it costs a string scan. What false suppresses is its USE: the composed prompt is forced
    /// to <see cref="ChatRoute.Union"/>, which is defined to be byte-identical to what shipped before
    /// routing existed. So a deployment that never sets this key composes exactly the prompt g4 and g5
    /// measured, and the log still says what the router would have done.</para>
    ///
    /// <para>IT IS ALSO THE ROLLBACK. g3's whole risk posture rests on the wording being revertable
    /// without a deploy, so nothing downstream may read the route from anywhere but the one place this
    /// flag gates.</para>
    ///
    /// <para>THE CLASS DEFAULT IS STILL <c>false</c> AND g2 DID NOT CHANGE IT, only the shipped
    /// <c>appsettings.json</c> value. That divergence is deliberate: every pin test in this suite
    /// constructs the service without thinking about the flag and must keep getting the inert Union
    /// posture, which is what makes those byte-identity fences meaningful. A test that wants the ROUTED
    /// behaviour asks for it explicitly, so "which prompt did this test measure" is always visible at the
    /// call site.</para>
    /// </summary>
    public bool RoutingEnabled { get; set; }

    /// <summary>
    /// THE GUIDE TOP SCORE BELOW WHICH AN ENGLISH PRODUCT TURN IS HANDED NO DOCUMENTS (g3d/gate 4), AND IT
    /// SHIPS AT 0, WHICH IS THE KILL SWITCH: no turn is withheld from. Gate run 5 measured the lever at 4.0
    /// and it produced four English answers asserting PageDraft behaviour that does not exist, against 0 in
    /// 408 prior records, while the metric it was built to move stayed put. The whole measurement is on
    /// <see cref="ProductChatRouter.EnglishProductDocumentsFloor"/> - READ IT BEFORE RAISING THIS.
    ///
    /// <para>IT IS CONFIG AND NOT A BARE CONST BECAUSE IT IS A NUMBER THE GATES ARGUE WITH, which is the
    /// same posture <see cref="RoutingEnabled"/> records for the routing layer as a whole: it was config so
    /// the lever could be turned off without a deploy, and that is exactly what happened.</para>
    ///
    /// <para>THE CLASS DEFAULT AND THE SHIPPED KEY ARE BOTH 0, AND IT IS THE SHIPPED KEY THAT BINDS IN EVERY
    /// ENVIRONMENT THIS APP RUNS IN. <c>Program.cs</c> builds the host with a plain
    /// <c>WebApplication.CreateBuilder(args)</c> and adds no configuration source of its own, so the default
    /// layering applies everywhere: <c>appsettings.json</c> FIRST, then <c>appsettings.{Environment}.json</c>
    /// on top of it, then environment variables, then the command line.
    /// <c>appsettings.Production.json</c> carries no <c>ProductChat</c> section, so nothing overrides the base
    /// file there and production binds <c>appsettings.json</c>'s 0. This class default is not what production
    /// reads; it is unreachable in any environment that loads <c>appsettings.json</c> at all. Moving
    /// <c>appsettings.json</c> is therefore exactly what DOES roll production back, and changing this default
    /// alone would not touch it.</para>
    ///
    /// <para>WHAT THIS DEFAULT IS ACTUALLY FOR: a host that loads no <c>appsettings.json</c>, which is a test
    /// that news up this options object, a bare <c>ConfigurationBuilder</c>, or a container built without the
    /// file. It is the floor for those callers, not for production. Both surfaces read 0 today so there is no
    /// behavioural difference between them either way, and the reason to keep them pinned together is the
    /// sibling flag: on <see cref="RoutingEnabled"/> the two DIVERGE (class default <c>false</c>, shipped key
    /// <c>true</c>), so a reader who believes production binds class defaults would "roll back" by flipping a
    /// default that is already <c>false</c> and leave <c>appsettings.json</c> shipping routing ON to
    /// production. <c>ProductChatRouterTests.TheShippedFloor_WithholdsOnNoTurn</c> pins the pair, and the pin
    /// is still worth having: it is what stops the floor from drifting into a divergence nobody chose.</para>
    /// </summary>
    public double EnglishProductDocumentsFloor { get; set; }
        = ProductChatRouter.EnglishProductDocumentsFloor;
}
