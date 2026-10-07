namespace Attendance.Api.Security;

/// <summary>
/// Marks an endpoint where a request signature is <b>optional</b>.
/// </summary>
/// <remarks>
/// <para>
/// Optional, not ignored. A request arriving without a signature is allowed
/// through; one arriving <em>with</em> a signature is verified in full. That
/// distinction matters: the startup location check must work before a device is
/// registered, but a signed call to it must still have its body digest checked,
/// and a registered device must still be recognised so it can be told which
/// office it matched.
/// </para>
/// <para>
/// Reserved for health probes, the registration-challenge endpoint, the app
/// configuration endpoint and the startup location check. It is an attribute
/// rather than a path list so the exemption sits on the endpoint itself, visible
/// to anyone reading the controller — a path list in configuration is somewhere
/// an exemption can be added without a code review noticing.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowUnsignedRequestAttribute : Attribute;

/// <summary>
/// Marks an endpoint that accepts a signature from a device that is not yet
/// registered (§6.3).
/// </summary>
/// <remarks>
/// <para>
/// Only device registration qualifies. The request is signed with the key being
/// registered, whose public key is in the body, so the middleware can check the
/// signature's shape, freshness and body digest but cannot resolve a device — the
/// device does not exist yet. Proof of possession is verified by the registration
/// handler, which has the presented public key.
/// </para>
/// <para>
/// Replay protection for these requests comes from the single-use registration
/// challenge rather than the nonce store, which is keyed by device.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowUnregisteredDeviceAttribute : Attribute;

/// <summary>
/// Marks an endpoint that a registered device may call even while it cannot act:
/// pending approval, revoked, or bound to an employee who is no longer active.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the device-status endpoint qualifies.</b> A device waiting for
/// approval has to be able to learn that it was approved, and a revoked one has to
/// be able to learn why it stopped working. Without this exemption the middleware
/// refused them before the endpoint ran, so a pending device could never discover
/// its approval — found by a test written from the client's point of view.
/// </para>
/// <para>
/// <b>Nothing else is relaxed.</b> The signature, nonce and body digest are
/// verified in full, so only the genuine device can ask, and only about itself.
/// An unregistered key is still refused: there is no stored key to verify the
/// signature against. The endpoint must not do anything but report.
/// </para>
/// <para>
/// Method-level only, so it cannot be applied to a whole controller by accident.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AllowInactiveDeviceAttribute : Attribute;

/// <summary>
/// The verified signature material handed to the registration handler.
/// </summary>
/// <param name="SignatureBase">The RFC 9421 signature base that was signed.</param>
/// <param name="Signature">The raw 64-byte r‖s signature.</param>
public sealed record UnregisteredSignature(byte[] SignatureBase, byte[] Signature)
{
    /// <summary>The <c>HttpContext.Items</c> key.</summary>
    public const string ContextKey = "ClockInXtra.UnregisteredSignature";
}
