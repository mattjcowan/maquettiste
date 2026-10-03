using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>Plan and apply as jobs, plans, diffs, jobs and previews (phase2-design.md sections 3.6 and 3.7), against the contract.</summary>
public sealed class GenerationTests
{
    private static async Task<JsonNode> CompletedAsync(EditorHost host, string jobId)
    {
        await EditorHost.WaitForAsync(() => host.Published("job.completed").Any(e => e.Target == "group:job:" + jobId), "job.completed of " + jobId, 60);
        return host.Published("job.completed").Single(e => e.Target == "group:job:" + jobId).Payload;
    }

    [Fact]
    public async Task Plan_then_apply_run_as_jobs_with_progress_and_completion_events()
    {
        await using var host = EditorHost.Create();
        host.StartBackground();

        var queued = await host.SendJsonAsync("POST", "/api/generate/plan", "{}");
        Assert.Equal(202, queued.Status);
        Contract.AssertResponse(queued, "/api/generate/plan");
        var planJobId = queued.Json["id"]!.GetValue<string>();
        Assert.Equal("/api/jobs/" + planJobId, queued.Headers.Location.ToString());
        Assert.Equal("plan", queued.Json["kind"]!.GetValue<string>());
        Recorder.Json("job-plan-queued.json", queued);

        var planned = await CompletedAsync(host, planJobId);
        Contract.AssertEvent("job.completed", planned);
        Assert.Equal("succeeded", planned["state"]!.GetValue<string>());
        Assert.Equal("succeeded", planned["outcome"]!.GetValue<string>());
        var planId = planned["planId"]!.GetValue<string>();
        Assert.True(planned["counts"]!["changes"]!.GetValue<int>() > 0);

        var progress = host.Published("job.progress").Where(e => e.Target == "group:job:" + planJobId).ToList();
        Assert.NotEmpty(progress);
        foreach (var (_, payload) in progress)
        {
            Contract.AssertEvent("job.progress", payload);
            Assert.Equal(planJobId, payload["id"]!.GetValue<string>());
            Assert.NotNull(payload["progress"]);
        }

        var job = await host.GetAsync("/api/jobs/" + planJobId);
        Contract.AssertResponse(job, "/api/jobs/{id}");
        Assert.Equal(planId, job.Json["planResult"]!["plan"]!["id"]!.GetValue<string>());
        Recorder.Json("job-plan-succeeded.json", job);

        var plan = await host.GetAsync("/api/generate/plan/" + planId);
        var withUnits = await host.GetAsync("/api/generate/plan/" + planId + "?units=true");
        Assert.Equal(200, plan.Status);
        Contract.AssertResponse(plan, "/api/generate/plan/{id}");
        Contract.AssertResponse(withUnits, "/api/generate/plan/{id}");
        Assert.Empty(plan.Json["units"]!.AsArray());
        Assert.NotEmpty(withUnits.Json["units"]!.AsArray());
        Assert.Equal(planned["counts"]!["changes"]!.GetValue<int>(), plan.Json["changes"]!.AsArray().Count);
        Assert.All(plan.Json["changes"]!.AsArray(), c => Assert.Null(c!["diff"]));
        Assert.False(plan.Json["request"]!["includeDiffs"]!.GetValue<bool>());
        Recorder.Json("plan.json", plan);

        var path = plan.Json["changes"]!.AsArray().First(c => c!["path"]!.GetValue<string>().EndsWith("customers.sql", StringComparison.Ordinal))!["path"]!.GetValue<string>();
        var diff = await host.GetAsync($"/api/generate/plan/{planId}/diff?path={Uri.EscapeDataString(path)}");
        Assert.Equal(200, diff.Status);
        Assert.StartsWith("text/x-diff", diff.ContentType, StringComparison.Ordinal);
        Contract.AssertResponse(diff, "/api/generate/plan/{id}/diff");
        Assert.Contains("+++ b/" + path, diff.Text, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE", diff.Text, StringComparison.Ordinal);
        Recorder.Text("plan-diff-customers.diff", diff);

        var apply = await host.SendJsonAsync("POST", "/api/generate/apply", new JsonObject { ["planId"] = planId });
        Assert.Equal(202, apply.Status);
        Contract.AssertResponse(apply, "/api/generate/apply");
        var applyJobId = apply.Json["id"]!.GetValue<string>();
        var applied = await CompletedAsync(host, applyJobId);
        Contract.AssertEvent("job.completed", applied);
        Assert.Equal("succeeded", applied["outcome"]!.GetValue<string>());
        Assert.Null(applied["planId"]);
        Assert.True(File.Exists(host.PathOf(path)));
        var applyJob = await host.GetAsync("/api/jobs/" + applyJobId);
        Contract.AssertResponse(applyJob, "/api/jobs/{id}");
        Assert.Equal("succeeded", applyJob.Json["applyResult"]!["outcome"]!.GetValue<string>());
        Recorder.Json("job-apply-succeeded.json", applyJob);

        var list = await host.GetAsync("/api/jobs");
        Contract.AssertResponse(list, "/api/jobs");
        Assert.Equal([applyJobId, planJobId], list.Json.AsArray().Select(j => j!["id"]!.GetValue<string>()).Take(2));
        Recorder.Json("jobs.json", list);
    }

    [Fact]
    public async Task An_apply_after_a_model_change_is_stale_in_a_job_that_succeeded()
    {
        await using var host = EditorHost.Create();
        host.StartBackground();
        var queued = await host.SendJsonAsync("POST", "/api/generate/plan", """{ "packs": ["sql-ddl"] }""");
        var planned = await CompletedAsync(host, queued.Json["id"]!.GetValue<string>());
        var customer = await host.GetAsync("/api/model/elements/" + EditorHost.CustomerId);
        var json = customer.Json["json"]!.AsObject();
        json["attributes"]![1]!["length"] = 200;
        var saved = await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.CustomerId, json, r => r.IfMatch(customer.Json["hash"]!.GetValue<string>()));
        Assert.Equal(200, saved.Status);

        var apply = await host.SendJsonAsync("POST", "/api/generate/apply", new JsonObject { ["planId"] = planned["planId"]!.GetValue<string>() });
        var applied = await CompletedAsync(host, apply.Json["id"]!.GetValue<string>());

        Contract.AssertEvent("job.completed", applied);
        Assert.Equal("succeeded", applied["state"]!.GetValue<string>());
        Assert.Equal("stale", applied["outcome"]!.GetValue<string>());
        Assert.True(applied["counts"]!["staleUnits"]!.GetValue<int>() > 0);
        var job = await host.GetAsync("/api/jobs/" + apply.Json["id"]!.GetValue<string>());
        Contract.AssertResponse(job, "/api/jobs/{id}");
        Assert.Equal(applied["counts"]!["staleUnits"]!.GetValue<int>(), job.Json["applyResult"]!["staleUnits"]!.AsArray().Count);
        Assert.False(File.Exists(host.PathOf("db/main/billing/tables/customers.sql")));
    }

    [Fact]
    public async Task Plan_and_apply_refuse_bad_bodies()
    {
        await using var host = EditorHost.Create();

        var badPlan = await host.SendJsonAsync("POST", "/api/generate/plan", "{ \"handEdits\": \"sometimes\" }");
        var badJobs = await host.SendJsonAsync("POST", "/api/generate/plan", "{ \"jobs\": 0 }");
        var noPlanId = await host.SendJsonAsync("POST", "/api/generate/apply", "{}");
        var badPlanId = await host.SendJsonAsync("POST", "/api/generate/apply", "{ \"planId\": \"plan-1\" }");
        var empty = await host.SendAsync(TestRequest.Local("POST", "/api/generate/apply").With(r => r.ContentType = "application/json"));

        foreach (var (response, template) in new[] { (badPlan, "/api/generate/plan"), (badJobs, "/api/generate/plan"), (noPlanId, "/api/generate/apply"),
                     (badPlanId, "/api/generate/apply"), (empty, "/api/generate/apply") })
        {
            Assert.Equal(400, response.Status);
            Assert.Equal("bad-request", response.ProblemCode);
            Contract.AssertResponse(response, template);
        }
    }

    [Fact]
    public async Task A_plan_with_no_body_takes_every_default()
    {
        await using var host = EditorHost.Create();

        var queued = await host.SendAsync(TestRequest.Local("POST", "/api/generate/plan").With(r => r.ContentType = "application/json"));

        Assert.Equal(202, queued.Status);
        var job = await host.Queue.GetAsync(queued.Json["id"]!.GetValue<string>(), EditorHost.Ct);
        Assert.Equal(JobKind.Plan, job!.Kind);
    }

    [Fact]
    public async Task Unknown_plans_paths_and_jobs_are_404()
    {
        await using var host = EditorHost.Create();

        var plan = await host.GetAsync("/api/generate/plan/01M3MNY0HY08AVTKW927JJPSEF");
        var notUlid = await host.GetAsync("/api/generate/plan/..%2F..%2Fetc");
        var diff = await host.GetAsync("/api/generate/plan/01M3MNY0HY08AVTKW927JJPSEF/diff?path=db/x.sql");
        var job = await host.GetAsync("/api/jobs/01M3MNY0HTVJQSH4BX78RWN8F7");
        var cancel = await host.SendAsync(TestRequest.Local("DELETE", "/api/jobs/01M3MNY0HTVJQSH4BX78RWN8F7"));

        Assert.Equal(404, plan.Status);
        Contract.AssertResponse(plan, "/api/generate/plan/{id}");
        Assert.Equal(404, notUlid.Status);
        Assert.Equal(404, diff.Status);
        Contract.AssertResponse(diff, "/api/generate/plan/{id}/diff");
        Assert.Equal(404, job.Status);
        Contract.AssertResponse(job, "/api/jobs/{id}");
        Assert.Equal(404, cancel.Status);
        Contract.AssertResponse(cancel, "/api/jobs/{id}");
    }

    [Fact]
    public async Task A_path_the_plan_does_not_name_has_no_diff()
    {
        await using var host = EditorHost.Create();
        host.StartBackground();
        var queued = await host.SendJsonAsync("POST", "/api/generate/plan", """{ "packs": ["sql-ddl"] }""");
        var planned = await CompletedAsync(host, queued.Json["id"]!.GetValue<string>());

        var diff = await host.GetAsync($"/api/generate/plan/{planned["planId"]!.GetValue<string>()}/diff?path=.maquettiste/maquettiste.json");
        var noPath = await host.GetAsync($"/api/generate/plan/{planned["planId"]!.GetValue<string>()}/diff");

        Assert.Equal(404, diff.Status);
        Assert.Equal("not-found", diff.ProblemCode);
        Assert.Equal(404, noPath.Status);
    }

    [Fact]
    public async Task Cancel_removes_a_queued_job_and_a_finished_job_is_409()
    {
        await using var host = EditorHost.Create(); // no worker: jobs stay queued
        var queued = await host.SendJsonAsync("POST", "/api/generate/plan", "{}");
        var id = queued.Json["id"]!.GetValue<string>();

        var cancelled = await host.SendAsync(TestRequest.Local("DELETE", "/api/jobs/" + id));
        var again = await host.SendAsync(TestRequest.Local("DELETE", "/api/jobs/" + id));

        Assert.Equal(202, cancelled.Status);
        Contract.AssertResponse(cancelled, "/api/jobs/{id}");
        Assert.Equal("cancelled", cancelled.Json["state"]!.GetValue<string>());
        Assert.Equal(409, again.Status);
        Assert.Equal("job-finished", again.ProblemCode);
        Contract.AssertResponse(again, "/api/jobs/{id}");
    }

    [Fact]
    public async Task Clearing_the_history_removes_finished_jobs_and_plans_and_tells_every_window()
    {
        await using var host = EditorHost.Create();
        host.StartBackground();
        var queued = await host.SendJsonAsync("POST", "/api/generate/plan", "{}");
        var planId = (await CompletedAsync(host, queued.Json["id"]!.GetValue<string>()))["planId"]!.GetValue<string>();

        var cleared = await host.SendAsync(TestRequest.Local("DELETE", "/api/jobs"));

        Assert.Equal(200, cleared.Status);
        Contract.AssertResponse(cleared, "/api/jobs");
        Assert.Equal(1, cleared.Json["jobs"]!.GetValue<int>());
        Assert.Equal(1, cleared.Json["plans"]!.GetValue<int>());
        var published = Assert.Single(host.Published("jobs.cleared"));
        Contract.AssertEvent("jobs.cleared", published.Payload);
        Assert.Equal(1, published.Payload["plans"]!.GetValue<int>());
        Assert.Empty((await host.GetAsync("/api/jobs")).Json.AsArray());
        Assert.Equal(404, (await host.GetAsync("/api/generate/plan/" + planId)).Status);
    }

    [Fact]
    public async Task A_seventeenth_queued_job_is_503_queue_full_with_retry_after()
    {
        await using var host = EditorHost.Create(); // no worker: jobs stay queued
        for (var i = 0; i < 16; i++)
            Assert.Equal(202, (await host.SendJsonAsync("POST", "/api/generate/plan", "{}")).Status);

        var full = await host.SendJsonAsync("POST", "/api/generate/plan", "{}");
        var fullApply = await host.SendJsonAsync("POST", "/api/generate/apply", """{ "planId": "01M3MNY0HY08AVTKW927JJPSEF" }""");

        Assert.Equal(503, full.Status);
        Assert.Equal("queue-full", full.ProblemCode);
        Assert.Equal("5", full.Headers.RetryAfter.ToString());
        Contract.AssertResponse(full, "/api/generate/plan");
        Assert.Equal(503, fullApply.Status);
        Contract.AssertResponse(fullApply, "/api/generate/apply");
    }

    [Fact]
    public async Task Preview_renders_a_unit_with_no_writes()
    {
        await using var host = EditorHost.Create();
        var before = Directory.EnumerateFiles(host.RepoRoot, "*", SearchOption.AllDirectories).Count();

        var table = await host.SendJsonAsync("POST", "/api/templates/preview", new JsonObject
        {
            ["pack"] = "sql-ddl", ["unit"] = "table", ["elementId"] = EditorHost.CustomerId + "@" + EditorHost.MainDatabaseId,
        });
        var schema = await host.SendJsonAsync("POST", "/api/templates/preview", new JsonObject { ["pack"] = "sql-ddl", ["unit"] = "schema", ["elementId"] = EditorHost.MainDatabaseId });
        var wrong = await host.SendJsonAsync("POST", "/api/templates/preview", new JsonObject { ["pack"] = "sql-ddl", ["unit"] = "table", ["elementId"] = EditorHost.InvoiceId });
        var missing = await host.SendJsonAsync("POST", "/api/templates/preview", new JsonObject { ["pack"] = "sql-ddl" });

        Assert.Equal(200, table.Status);
        Contract.AssertResponse(table, "/api/templates/preview");
        var file = table.Json["files"]!.AsArray().Single()!;
        Assert.Equal("db/main/billing/tables/customers.sql", file["path"]!.GetValue<string>());
        Assert.Contains("CREATE TABLE billing.customers", file["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(200, schema.Status);
        Contract.AssertResponse(schema, "/api/templates/preview");
        Assert.Equal(200, wrong.Status);
        Contract.AssertResponse(wrong, "/api/templates/preview");
        Assert.Empty(wrong.Json["files"]!.AsArray());
        Assert.NotEmpty(wrong.Json["diagnostics"]!.AsArray());
        Assert.Equal(400, missing.Status);
        Contract.AssertResponse(missing, "/api/templates/preview");
        Assert.Equal(before, Directory.EnumerateFiles(host.RepoRoot, "*", SearchOption.AllDirectories).Count());
        Recorder.Json("preview-sql-ddl-table-customers.json", table);
        Recorder.Json("preview-sql-ddl-schema-main.json", schema);
    }
}
