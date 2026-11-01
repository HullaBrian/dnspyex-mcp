# Debugger (dnSpy.Contracts.Debugger)

`Debugger` is dnSpy's DbgManager, or null if the debugger extension isn't loaded. Debugger events are raised
on Debugger.Dispatcher (DbgDispatcher), not the UI thread.

Breakpoints (can be set before debugging starts)
  SetBreakpoint(Method("App.Program::Main"))                     enabled breakpoint at IL offset 0
  SetBreakpoint("App.Crypto::Decrypt", ilOffset: 0x12)           offsets come from IL(method)
  SetBreakpoint(m, 0, tracepointMessage: "Decrypt called")       tracepoint (logs, doesn't break)
  var bps = Resolve<dnSpy.Contracts.Debugger.Breakpoints.Code.DbgCodeBreakpointsService>();
  bps.Breakpoints (DbgCodeBreakpoint[]), bps.Remove(bps.Breakpoints), bps.Clear()

Start / control
  using dnSpy.Contracts.Debugger.DotNet.CorDebug;
  Debugger.Start(new DotNetFrameworkStartDebuggingOptions { Filename = exe, CommandLine = "", WorkingDirectory = dir,
                                                           BreakKind = dnSpy.Contracts.Debugger.PredefinedBreakKinds.EntryPoint })
    returns an error string or null. Use DotNetStartDebuggingOptions for .NET (Core) apps.
  Debugger.BreakAll(), RunAll(), StopDebuggingAll(), DetachAll(), TerminateAll(), Restart()

State
  Debugger.IsDebugging, Debugger.IsRunning (bool?; null = some processes running), Debugger.Processes (DbgProcess[])
  Debugger.CurrentProcess / CurrentThread / CurrentRuntime   -> DbgCurrentObject<T> { Current (what the UI shows), Break }
  DbgProcess: Id, Name, Filename, State, Runtimes, Threads (DbgThread[]), Break(), Run(), Detach(), Terminate()
  DbgThread: Id, ManagedId, Name, Process, Runtime, GetFrames(count) -> DbgStackFrame[]  (Close() frames when done)
  DbgStackFrame: Function, Location (DbgCodeLocation), FunctionOffset
  Events: IsDebuggingChanged, IsRunningChanged, ProcessesChanged, DelayedIsRunningChanged, Message

Waiting for a break from a script
  var tcs = new TaskCompletionSource();
  EventHandler h = (s, e) => { if (Debugger.IsRunning == false) tcs.TrySetResult(); };
  Debugger.IsRunningChanged += h;
  try { await Task.WhenAny(tcs.Task, Task.Delay(30000, Token)); } finally { Debugger.IsRunningChanged -= h; }

Exact signatures: reference("DbgManager"), reference("DbgProcess"), reference("DbgThread"),
reference("DbgCodeBreakpointsService"), reference("DbgDotNetBreakpointFactory").
