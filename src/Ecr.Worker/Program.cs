// src/Ecr.Worker/Program.cs

using Ecr.Worker;

return await WorkerProgram.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
