using System;
using App.Model.Runtime;

namespace App.Model.Requests;

// The hand-written half of RequestLifecycleHandlers. Maquettiste wrote this file once and never touches it again. Implement here the
// guards and actions whose expression the generated half could not translate (or that have none); a guard or action added to
// the model later is declared in RequestLifecycleHandlers.g.cs and fails the build until you add it here.
public sealed partial class RequestLifecycleHandlers
{
    /// <summary>Guard notTheSubmitter.</summary>
    private partial bool GuardNotTheSubmitter(StatechartCall<RequestLifecycleContext> call) =>
        throw new NotImplementedException("Guard notTheSubmitter of process RequestLifecycle is not implemented yet.");

    /// <summary>Guard employeeActive.</summary>
    private partial bool GuardEmployeeActive(StatechartCall<RequestLifecycleContext> call) =>
        throw new NotImplementedException("Guard employeeActive of process RequestLifecycle is not implemented yet.");

    /// <summary>Action recordSubmission.</summary>
    private partial RequestLifecycleContext ActionRecordSubmission(StatechartCall<RequestLifecycleContext> call) =>
        throw new NotImplementedException("Action recordSubmission of process RequestLifecycle is not implemented yet.");

    // Record on the object what action recordSubmission sets: submittedBy = current_actor(), submittedOn = now(). Each value reads the object and call.Context as they were
    // before the action; the generated half runs this, then the action.
    private partial void RecordRecordSubmission(StatechartCall<RequestLifecycleContext> call) =>
        throw new NotImplementedException("Action recordSubmission of process RequestLifecycle does not record its sets yet.");

    // Record on the object what action bump sets: note = 'bumped'. Each value reads the object and call.Context as they were
    // before the action; the generated half runs this, then the action.
    private partial void RecordBump(StatechartCall<RequestLifecycleContext> call) =>
        throw new NotImplementedException("Action bump of process RequestLifecycle does not record its sets yet.");

    /// <summary>Action notify.</summary>
    private partial RequestLifecycleContext ActionNotify(StatechartCall<RequestLifecycleContext> call) =>
        throw new NotImplementedException("Action notify of process RequestLifecycle is not implemented yet.");

    /// <summary>Action stamp.</summary>
    // The model's expression, outside the translated subset: ({ count: [context.count, 1].length })
    private partial RequestLifecycleContext ActionStamp(StatechartCall<RequestLifecycleContext> call) =>
        throw new NotImplementedException("Action stamp of process RequestLifecycle is not implemented yet.");

    // Record on the object what action stamp sets: note = 'stamped'. Each value reads the object and call.Context as they were
    // before the action; the generated half runs this, then the action.
    private partial void RecordStamp(StatechartCall<RequestLifecycleContext> call) =>
        throw new NotImplementedException("Action stamp of process RequestLifecycle does not record its sets yet.");
}
