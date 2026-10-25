namespace CheapHours.Providers.Ocpp.Framing;

/// <summary>The CallError codes of OCPP-J 1.6, spelt exactly as on the wire (including "Occurence").</summary>
public enum OcppErrorCode
{
    /// <summary>The action is not known to the receiver.</summary>
    NotImplemented,

    /// <summary>The action is known but not supported by the receiver.</summary>
    NotSupported,

    /// <summary>The receiver failed while handling a valid call.</summary>
    InternalError,

    /// <summary>The payload is incomplete.</summary>
    ProtocolError,

    /// <summary>A security issue prevented the receiver from handling the call.</summary>
    SecurityError,

    /// <summary>The message or payload is syntactically wrong.</summary>
    FormationViolation,

    /// <summary>A field holds a value outside its constraints (length, enum, format).</summary>
    PropertyConstraintViolation,

    /// <summary>A required field is missing (the OCPP 1.6 spelling).</summary>
    OccurenceConstraintViolation,

    /// <summary>A field has the wrong type.</summary>
    TypeConstraintViolation,

    /// <summary>Any other error.</summary>
    GenericError,
}
