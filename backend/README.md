# Exam Maker backend

From the repository root, open PowerShell and run:

```powershell
Set-Location backend
dotnet build ExamMaker.slnx
dotnet run --project src/ExamMaker.Api --urls http://localhost:5080
```

In a second terminal, run `Invoke-WebRequest http://localhost:5080/health`.
The health endpoint returns HTTP 200 when the API is running.

Start the worker with `dotnet run --project src/ExamMaker.Worker` from
`backend/`. The host starts and waits for shutdown. Job processing will
be registered when the AI pipeline is implemented.

## Configuration

Both hosts read application settings and environment variables. Local runs
can leave `ParameterStore__Path` unset and start without AWS access. To
enable AWS Systems Manager Parameter Store, set `ParameterStore__Path`
to the deployment's agreed prefix and configure the AWS region and
credentials for the runtime identity. Environment variables override
values loaded from Parameter Store.

## Project layout

Keep HTTP mapping in API controllers and put each use case in Application.
Domain owns business rules. Infrastructure implements integrations.
The API and Worker are separate entry points. Add backend test projects
under `tests/`.
