using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PadForge.Engine.Common.OpenXr;
using static PadForge.Engine.Common.OpenXr.OpenXrInterop;

namespace PadForge.Tests
{
    /// <summary>
    /// A managed stand-in for an OpenXR runtime library, for the tests that
    /// drive the real <see cref="OpenXrSession"/>. It hands out the function
    /// pointers a runtime hands out and answers them from a script, so no
    /// library is loaded and no runtime is started. Its manifest and an empty
    /// file under its library's name sit in a folder of their own, since the
    /// session reads both from disk before it asks for the library.
    ///
    /// <para>It is strict about handles. A call on a session, a space or an
    /// action set that was destroyed, or on an instance that is gone, lands
    /// in <see cref="Faults"/>, which a test asserts empty.</para>
    ///
    /// <para>One thread drives it, as one thread drives the session.</para>
    /// </summary>
    internal sealed class OpenXrScriptedRuntime : IDisposable
    {
        public const ulong InstanceHandle = 0x100;
        public const ulong SystemHandle = 0x55;

        // Result codes the session has no name for, as openxr.h gives them.
        private const int XR_ERROR_FUNCTION_UNSUPPORTED = -7;
        private const int XR_ERROR_LIMIT_REACHED = -10;
        private const int XR_ERROR_HANDLE_INVALID = -12;
        private const int XR_ERROR_SESSION_RUNNING = -14;
        private const int XR_ERROR_SYSTEM_INVALID = -18;
        private const int XR_ERROR_SESSION_NOT_READY = -28;
        private const int XR_ERROR_SESSION_NOT_STOPPING = -29;
        private const int XR_ERROR_NAME_DUPLICATED = -44;
        private const int XR_ERROR_ACTIONSETS_ALREADY_ATTACHED = -47;

        private readonly string _folder;
        private readonly Dictionary<string, Delegate> _functions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IntPtr> _pointers = new(StringComparer.Ordinal);
        private readonly PFN_xrGetInstanceProcAddr _getProc;

        private ulong _nextHandle = 0x1000;
        private ulong _session;
        private int _sessionState = XR_SESSION_STATE_IDLE;
        private bool _sessionRunning;
        private bool _sessionAttached;
        private readonly HashSet<ulong> _spaces = new();
        private readonly HashSet<ulong> _viewSpaces = new();
        private readonly HashSet<ulong> _actionSets = new();
        private readonly HashSet<ulong> _attachedSets = new();
        private readonly Dictionary<ulong, ulong> _actions = new();
        private readonly Dictionary<string, ulong> _paths = new(StringComparer.Ordinal);
        private readonly Queue<(int Type, ulong Session, int State, long LossTime)> _events = new();

        // The script, set by a test before the session asks.

        /// <summary>What the first call of the extension listing answers,
        /// the one that asks for the count.</summary>
        public int Listing = XR_SUCCESS;

        /// <summary>What its second call answers, the one that fills the
        /// list.</summary>
        public int ListingFill = XR_SUCCESS;

        public string[] Extensions =
        {
            XR_MND_HEADLESS_EXTENSION_NAME,
            XR_KHR_WIN32_CONVERT_PERFORMANCE_COUNTER_TIME_EXTENSION_NAME,
        };

        /// <summary>Function names this runtime does not hand out.</summary>
        public readonly HashSet<string> Missing = new(StringComparer.Ordinal);

        /// <summary>What xrCreateInstance answers, one per ask, and success
        /// once these run out.</summary>
        public readonly Queue<int> InstanceAnswers = new();

        /// <summary>The name xrGetInstanceProperties gives, or null for a
        /// runtime that gives none. It starts as the manifest's name.</summary>
        public string InstanceName;

        /// <summary>What xrGetSystem answers, one per ask, and success
        /// once these run out.</summary>
        public readonly Queue<int> SystemAnswers = new();

        /// <summary>What xrCreateSession answers, one per ask, and success
        /// once these run out.</summary>
        public readonly Queue<int> SessionAnswers = new();

        /// <summary>What xrBeginSession answers, one per ask, and success
        /// once these run out. A success code other than XR_SUCCESS still
        /// begins the session, as a lost session simulates success.</summary>
        public readonly Queue<int> BeginAnswers = new();

        /// <summary>What xrEndSession answers, one per ask, and success
        /// once these run out.</summary>
        public readonly Queue<int> EndAnswers = new();

        /// <summary>What xrSuggestInteractionProfileBindings answers for
        /// every profile, what xrAttachSessionActionSets answers, and what
        /// xrCreateActionSpace answers. A runtime with no controller profile
        /// to accept, or one that will not take the action set, leaves a
        /// session with no controllers. A success code other than XR_SUCCESS
        /// still attaches the set and hands out the space, as a session
        /// being lost simulates success.</summary>
        public int SuggestAnswer = XR_SUCCESS;
        public int AttachAnswer = XR_SUCCESS;
        public int ActionSpaceAnswer = XR_SUCCESS;

        /// <summary>True for a runtime whose one session handle has the same
        /// value every time, as VirtualDesktop-OpenXR's has
        /// (session.cpp, <c>*session = (XrSession)1</c>).</summary>
        public bool ReusesTheSessionHandle;

        /// <summary>What xrCreateReferenceSpace answers for a reference space
        /// type, and success for a type not listed. A success code other than
        /// XR_SUCCESS still hands out a space, as the specification has a
        /// lost session simulate success.</summary>
        public readonly Dictionary<int, int> SpaceAnswers = new();

        /// <summary>What xrPollEvent answers in place of reading its queue,
        /// when it is not XR_SUCCESS.</summary>
        public int PollAnswer = XR_SUCCESS;

        /// <summary>Now on the runtime's clock, as the time conversion gives
        /// it. It is a number a test sets, so nothing here reads a real
        /// clock.</summary>
        public long Now = 5_000_000_000_000;

        /// <summary>Where the headset is, as xrLocateSpace gives it for the
        /// view space. It starts at the origin, looking straight ahead.</summary>
        public XrPosef Head = new() { orientation = new XrQuaternionf { w = 1f } };

        /// <summary>Runs when a session is created, with its number from 1.</summary>
        public Action<int> OnSessionCreated { get; set; }

        /// <summary>Runs when xrPollEvent finds the queue empty. Events it
        /// queues are read by that same poll.</summary>
        public Action OnIdlePoll { get; set; }

        /// <summary>Runs when the library is asked for, before the answer.</summary>
        public Action<OpenXrRuntimeEntry> OnLoad { get; set; }

        /// <summary>False for a library that will not load.</summary>
        public bool Loads = true;

        // What happened, read by a test afterwards.

        public readonly List<string> Faults = new();

        /// <summary>The instance, session and system calls in order, by name.</summary>
        public readonly List<string> Calls = new();

        /// <summary>The library paths the session asked for, in order.</summary>
        public readonly List<string> Loaded = new();

        public bool InstanceAlive { get; private set; }
        public int InstancesCreated, InstancesDestroyed;
        public int SessionsCreated, SessionsDestroyed;
        public int ActionSetsCreated, ActionSetsDestroyed;
        public int Begins, BeginAsks, Ends, SystemAsks, Polls;
        public long LastLocateTime;

        public ulong LiveSession => _session;
        public int LiveSpaces => _spaces.Count;
        public int LiveActionSets => _actionSets.Count;

        public string ManifestPath { get; }
        public string LibraryPath { get; private set; }
        public string Name { get; }

        public OpenXrScriptedRuntime(string name = "Scripted Runtime", string libraryFile = "scripted-runtime.dll")
        {
            Name = name;
            InstanceName = name;
            _folder = Path.Combine(Path.GetTempPath(), "padforge-openxr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            ManifestPath = Path.Combine(_folder, "runtime.json");
            PointManifestAt(libraryFile);

            _getProc = GetProc;
            Add<PFN_xrEnumerateInstanceExtensionProperties>("xrEnumerateInstanceExtensionProperties", Enumerate);
            Add<PFN_xrCreateInstance>("xrCreateInstance", CreateInstance);
            Add<PFN_xrDestroyInstance>("xrDestroyInstance", DestroyInstance);
            Add<PFN_xrGetInstanceProperties>("xrGetInstanceProperties", GetInstanceProperties);
            Add<PFN_xrGetSystem>("xrGetSystem", GetSystem);
            Add<PFN_xrCreateSession>("xrCreateSession", CreateSession);
            Add<PFN_xrDestroySession>("xrDestroySession", DestroySession);
            Add<PFN_xrBeginSession>("xrBeginSession", BeginSession);
            Add<PFN_xrEndSession>("xrEndSession", EndSession);
            Add<PFN_xrCreateReferenceSpace>("xrCreateReferenceSpace", CreateReferenceSpace);
            Add<PFN_xrDestroySpace>("xrDestroySpace", DestroySpace);
            Add<PFN_xrLocateSpace>("xrLocateSpace", LocateSpace);
            Add<PFN_xrPollEvent>("xrPollEvent", PollEvent);
            Add<PFN_xrConvertWin32PerformanceCounterToTimeKHR>(
                "xrConvertWin32PerformanceCounterToTimeKHR", ConvertTime);
            Add<PFN_xrCreateActionSet>("xrCreateActionSet", CreateActionSet);
            Add<PFN_xrDestroyActionSet>("xrDestroyActionSet", DestroyActionSet);
            Add<PFN_xrCreateAction>("xrCreateAction", CreateAction);
            Add<PFN_xrStringToPath>("xrStringToPath", StringToPath);
            Add<PFN_xrSuggestInteractionProfileBindings>("xrSuggestInteractionProfileBindings", Suggest);
            Add<PFN_xrAttachSessionActionSets>("xrAttachSessionActionSets", Attach);
            Add<PFN_xrSyncActions>("xrSyncActions", Sync);
            Add<PFN_xrGetActionStatePose>("xrGetActionStatePose", GetPose);
            Add<PFN_xrGetActionStateFloat>("xrGetActionStateFloat", GetFloat);
            Add<PFN_xrGetActionStateBoolean>("xrGetActionStateBoolean", GetBoolean);
            Add<PFN_xrGetActionStateVector2f>("xrGetActionStateVector2f", GetVector2f);
            Add<PFN_xrCreateActionSpace>("xrCreateActionSpace", CreateActionSpace);
        }

        private void Add<T>(string name, T function) where T : Delegate
        {
            // The delegate is kept, so the pointer made from it stays good.
            _functions[name] = function;
            _pointers[name] = Marshal.GetFunctionPointerForDelegate(function);
        }

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // The files the session reads before it asks for the library.

        /// <summary>Writes the manifest to name a library in its own folder,
        /// and puts an empty file there under that name.</summary>
        public void PointManifestAt(string libraryFile)
        {
            LibraryPath = Path.Combine(_folder, libraryFile);
            if (!File.Exists(LibraryPath)) File.WriteAllBytes(LibraryPath, Array.Empty<byte>());
            File.WriteAllText(ManifestPath,
                "{ \"file_format_version\": \"1.0.0\", \"runtime\": { \"name\": \"" + Name
                + "\", \"library_path\": \"" + libraryFile + "\" } }");
        }

        public void TakeManifestOffDisk() => File.Delete(ManifestPath);

        public void TakeLibraryOffDisk() => File.Delete(LibraryPath);

        /// <summary>The runtime as the registry would list it.</summary>
        public OpenXrRuntimeEntry Entry =>
            OpenXrRuntimeCatalog.TryParseManifest(ManifestPath, File.ReadAllText(ManifestPath));

        /// <summary>Stands in for loading the library and negotiating with
        /// it (<see cref="OpenXrSession.RuntimeLoader"/>).</summary>
        public PFN_xrGetInstanceProcAddr Load(OpenXrRuntimeEntry entry, out string read)
        {
            OnLoad?.Invoke(entry);
            Loaded.Add(entry.LibraryPath);
            read = Loads ? null : $"the library of {entry.Name}, {entry.LibraryPath}, did not load";
            return Loads ? _getProc : null;
        }

        /// <summary>Opens the real session on this runtime by its manifest,
        /// with no other runtime registered. <paramref name="again"/> makes
        /// it the ask the source sends on its own after a retired instance.</summary>
        public OpenXrSession Open(Action<string> log, out OpenXrSourceState failure, out bool runtimeUnavailable,
                                  out string read, bool again = false)
            => OpenXrSession.TryCreate(ManifestPath, again, log, Load, () => Array.Empty<OpenXrRuntimeEntry>(),
                                       out failure, out runtimeUnavailable, out read);

        // Events, queued by a test and handed out by xrPollEvent.

        public void QueueSessionState(int state)
            => _events.Enqueue((XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED, _session, state, 0));

        /// <summary>A state event that names a session of the test's
        /// choosing, as a runtime at fault would send one.</summary>
        public void QueueSessionStateFor(ulong session, int state)
            => _events.Enqueue((XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED, session, state, 0));

        /// <summary>Moves the live session to a state with no event of its
        /// own, for a runtime whose events leave their session field empty.</summary>
        public void SetSessionState(int state) => _sessionState = state;

        public void QueueInstanceLoss(long lossTime)
            => _events.Enqueue((XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING, 0, 0, lossTime));

        /// <summary>A reference space change of the live session.</summary>
        public void QueueReferenceSpaceChange() => QueueReferenceSpaceChangeFor(_session);

        /// <summary>A reference space change that names a session of the
        /// test's choosing, as a runtime at fault would send one.</summary>
        public void QueueReferenceSpaceChangeFor(ulong session)
            => _events.Enqueue((XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING, session, 0, 0));

        /// <summary>An event of a type the session does not read. Only its
        /// type is written, and the rest of the buffer is whatever the
        /// last event left there.</summary>
        public void QueueEvent(int type) => _events.Enqueue((type, _session, 0, 0));

        private void Fault(string what) => Faults.Add(what);

        private int GetProc(ulong instance, string name, out IntPtr function)
        {
            // Before an instance stands, a runtime answers for the two calls
            // that need none.
            bool needsNone = name == "xrEnumerateInstanceExtensionProperties" || name == "xrCreateInstance";
            if (!needsNone && !(instance == InstanceHandle && InstanceAlive))
                Fault("xrGetInstanceProcAddr for " + name + " without a live instance");
            if (Missing.Contains(name) || !_pointers.TryGetValue(name, out function))
            {
                function = IntPtr.Zero;
                return XR_ERROR_FUNCTION_UNSUPPORTED;
            }
            return XR_SUCCESS;
        }

        private static void WriteName(IntPtr at, int capacity, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            int n = Math.Min(bytes.Length, capacity - 1);
            Marshal.Copy(bytes, 0, at, n);
            Marshal.WriteByte(at, n, 0);
        }

        // The instance.

        private int Enumerate(string layerName, uint capacity, out uint count, IntPtr properties)
        {
            count = 0;
            if (capacity == 0)
            {
                Calls.Add("xrEnumerateInstanceExtensionProperties");
                if (Listing != XR_SUCCESS) return Listing;
                count = (uint)Extensions.Length;
                return XR_SUCCESS;
            }
            if (ListingFill != XR_SUCCESS) return ListingFill;

            int stride = Marshal.SizeOf<XrExtensionProperties>();
            int nameAt = (int)Marshal.OffsetOf<XrExtensionProperties>("extensionName");
            for (int i = 0; i < Extensions.Length && i < capacity; i++)
            {
                IntPtr entry = properties + i * stride;
                if (Marshal.ReadInt32(entry) != XR_TYPE_EXTENSION_PROPERTIES)
                    Fault("an extension entry without its structure type");
                WriteName(entry + nameAt, XR_MAX_EXTENSION_NAME_SIZE, Extensions[i]);
                count++;
            }
            return XR_SUCCESS;
        }

        private int CreateInstance(ref XrInstanceCreateInfo info, out ulong instance)
        {
            instance = 0;
            Calls.Add("xrCreateInstance");
            if (info.type != XR_TYPE_INSTANCE_CREATE_INFO) Fault("xrCreateInstance without its structure type");
            int answer = InstanceAnswers.Count > 0 ? InstanceAnswers.Dequeue() : XR_SUCCESS;
            if (answer != XR_SUCCESS) return answer;
            if (InstanceAlive)
            {
                Fault("a second instance while one stands");
                return XR_ERROR_LIMIT_REACHED;
            }
            InstanceAlive = true;
            InstancesCreated++;
            instance = InstanceHandle;
            return XR_SUCCESS;
        }

        private bool CheckInstance(ulong instance, string call)
        {
            if (instance == InstanceHandle && InstanceAlive) return true;
            Fault(call + " on an instance that is gone");
            return false;
        }

        private int DestroyInstance(ulong instance)
        {
            Calls.Add("xrDestroyInstance");
            if (!CheckInstance(instance, "xrDestroyInstance")) return XR_ERROR_HANDLE_INVALID;
            if (_session != 0) Fault("the instance destroyed with a session standing");
            if (_spaces.Count != 0) Fault("the instance destroyed with spaces standing");
            if (_actionSets.Count != 0) Fault("the instance destroyed with an action set standing");
            InstanceAlive = false;
            InstancesDestroyed++;
            return XR_SUCCESS;
        }

        private int GetInstanceProperties(ulong instance, IntPtr properties)
        {
            if (!CheckInstance(instance, "xrGetInstanceProperties")) return XR_ERROR_HANDLE_INVALID;
            if (Marshal.ReadInt32(properties) != XR_TYPE_INSTANCE_PROPERTIES)
                Fault("xrGetInstanceProperties without its structure type");
            int nameAt = (int)Marshal.OffsetOf<XrInstanceProperties>("runtimeName");
            WriteName(properties + nameAt, XR_MAX_RUNTIME_NAME_SIZE, InstanceName);
            return XR_SUCCESS;
        }

        // The system and the session.

        private int GetSystem(ulong instance, ref XrSystemGetInfo info, out ulong systemId)
        {
            systemId = 0;
            SystemAsks++;
            Calls.Add("xrGetSystem");
            if (!CheckInstance(instance, "xrGetSystem")) return XR_ERROR_HANDLE_INVALID;
            if (info.type != XR_TYPE_SYSTEM_GET_INFO || info.formFactor != XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY)
                Fault("xrGetSystem asked for something other than a head-mounted display");
            int answer = SystemAnswers.Count > 0 ? SystemAnswers.Dequeue() : XR_SUCCESS;
            if (answer == XR_SUCCESS) systemId = SystemHandle;
            return answer;
        }

        private int CreateSession(ulong instance, ref XrSessionCreateInfo info, out ulong session)
        {
            session = 0;
            Calls.Add("xrCreateSession");
            if (!CheckInstance(instance, "xrCreateSession")) return XR_ERROR_HANDLE_INVALID;
            if (info.type != XR_TYPE_SESSION_CREATE_INFO) Fault("xrCreateSession without its structure type");
            if (info.next != IntPtr.Zero) Fault("a graphics binding on a session that is to be headless");
            if (info.systemId != SystemHandle)
            {
                Fault("xrCreateSession on a system xrGetSystem did not give");
                return XR_ERROR_SYSTEM_INVALID;
            }
            int answer = SessionAnswers.Count > 0 ? SessionAnswers.Dequeue() : XR_SUCCESS;
            if (answer != XR_SUCCESS) return answer;
            if (_session != 0)
            {
                Fault("a second session while one stands");
                return XR_ERROR_LIMIT_REACHED;
            }
            _session = ReusesTheSessionHandle ? 1UL : _nextHandle++;
            _sessionState = XR_SESSION_STATE_IDLE;
            _sessionRunning = false;
            _sessionAttached = false;
            SessionsCreated++;
            session = _session;
            OnSessionCreated?.Invoke(SessionsCreated);
            return XR_SUCCESS;
        }

        private bool CheckSession(ulong session, string call)
        {
            if (session != 0 && session == _session) return true;
            Fault(call + " on a session that is gone (" + session + ")");
            return false;
        }

        private int DestroySession(ulong session)
        {
            Calls.Add("xrDestroySession");
            if (!CheckSession(session, "xrDestroySession")) return XR_ERROR_HANDLE_INVALID;
            // A session takes its spaces with it.
            _spaces.Clear();
            _viewSpaces.Clear();
            _session = 0;
            _sessionRunning = false;
            SessionsDestroyed++;
            return XR_SUCCESS;
        }

        private int BeginSession(ulong session, ref XrSessionBeginInfo info)
        {
            if (!CheckSession(session, "xrBeginSession")) return XR_ERROR_HANDLE_INVALID;
            if (_sessionState != XR_SESSION_STATE_READY)
            {
                Fault("xrBeginSession outside XR_SESSION_STATE_READY");
                return XR_ERROR_SESSION_NOT_READY;
            }
            if (_sessionRunning)
            {
                Fault("xrBeginSession on a running session");
                return XR_ERROR_SESSION_RUNNING;
            }
            BeginAsks++;
            int answer = BeginAnswers.Count > 0 ? BeginAnswers.Dequeue() : XR_SUCCESS;
            if (answer < 0) return answer;
            _sessionRunning = true;
            Begins++;
            return answer;
        }

        private int EndSession(ulong session)
        {
            if (!CheckSession(session, "xrEndSession")) return XR_ERROR_HANDLE_INVALID;
            if (_sessionState != XR_SESSION_STATE_STOPPING)
            {
                Fault("xrEndSession outside XR_SESSION_STATE_STOPPING");
                return XR_ERROR_SESSION_NOT_STOPPING;
            }
            int answer = EndAnswers.Count > 0 ? EndAnswers.Dequeue() : XR_SUCCESS;
            if (answer < 0) return answer;
            _sessionRunning = false;
            Ends++;
            return answer;
        }

        private int CreateReferenceSpace(ulong session, ref XrReferenceSpaceCreateInfo info, out ulong space)
        {
            space = 0;
            if (!CheckSession(session, "xrCreateReferenceSpace")) return XR_ERROR_HANDLE_INVALID;
            if (info.type != XR_TYPE_REFERENCE_SPACE_CREATE_INFO)
                Fault("xrCreateReferenceSpace without its structure type");
            int answer = SpaceAnswers.TryGetValue(info.referenceSpaceType, out int scripted) ? scripted : XR_SUCCESS;
            if (answer < 0) return answer;
            space = _nextHandle++;
            _spaces.Add(space);
            if (info.referenceSpaceType == XR_REFERENCE_SPACE_TYPE_VIEW) _viewSpaces.Add(space);
            return answer;
        }

        private int DestroySpace(ulong space)
        {
            _viewSpaces.Remove(space);
            if (_spaces.Remove(space)) return XR_SUCCESS;
            Fault("xrDestroySpace on a space that is gone (" + space + ")");
            return XR_ERROR_HANDLE_INVALID;
        }

        private int LocateSpace(ulong space, ulong baseSpace, long time, ref XrSpaceLocation location)
        {
            if (!_spaces.Contains(space) || !_spaces.Contains(baseSpace))
            {
                Fault("xrLocateSpace on a space that is gone");
                return XR_ERROR_HANDLE_INVALID;
            }
            LastLocateTime = time;
            // A session that has not begun locates nothing.
            if (!_sessionRunning) return XR_SUCCESS;
            location.locationFlags = XR_SPACE_LOCATION_ORIENTATION_VALID_BIT | XR_SPACE_LOCATION_POSITION_VALID_BIT
                                   | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT | XR_SPACE_LOCATION_POSITION_TRACKED_BIT;
            if (_viewSpaces.Contains(space)) location.pose = Head;
            else location.pose.orientation.w = 1f;
            return XR_SUCCESS;
        }

        private int PollEvent(ulong instance, IntPtr eventData)
        {
            if (!CheckInstance(instance, "xrPollEvent")) return XR_ERROR_HANDLE_INVALID;
            Polls++;
            if (Marshal.ReadInt32(eventData) != XR_TYPE_EVENT_DATA_BUFFER)
                Fault("xrPollEvent without the buffer's structure type");
            if (PollAnswer != XR_SUCCESS) return PollAnswer;
            if (_events.Count == 0) OnIdlePoll?.Invoke();
            if (_events.Count == 0) return XR_EVENT_UNAVAILABLE;

            var e = _events.Dequeue();
            switch (e.Type)
            {
                case XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED:
                    if (e.Session != 0 && e.Session == _session) _sessionState = e.State;
                    Marshal.StructureToPtr(new XrEventDataSessionStateChanged
                    {
                        type = e.Type,
                        session = e.Session,
                        state = e.State,
                        time = Now,
                    }, eventData, false);
                    break;
                case XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING:
                    Marshal.StructureToPtr(new XrEventDataInstanceLossPending
                    {
                        type = e.Type,
                        lossTime = e.LossTime,
                    }, eventData, false);
                    break;
                case XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING:
                    Marshal.StructureToPtr(new XrEventDataReferenceSpaceChangePending
                    {
                        type = e.Type,
                        session = e.Session,
                        referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE,
                        changeTime = Now,
                    }, eventData, false);
                    break;
                default:
                    Marshal.WriteInt32(eventData, e.Type);
                    break;
            }
            return XR_SUCCESS;
        }

        private int ConvertTime(ulong instance, ref long performanceCounter, out long time)
        {
            time = 0;
            if (!CheckInstance(instance, "xrConvertWin32PerformanceCounterToTimeKHR")) return XR_ERROR_HANDLE_INVALID;
            time = Now;
            return XR_SUCCESS;
        }

        // The actions, enough of them for the controllers to come up.

        private int CreateActionSet(ulong instance, ref XrActionSetCreateInfo info, out ulong set)
        {
            set = 0;
            if (!CheckInstance(instance, "xrCreateActionSet")) return XR_ERROR_HANDLE_INVALID;
            // PadForge gives its one action set the same name every time,
            // and a runtime refuses a name a standing set already has.
            if (_actionSets.Count != 0)
            {
                Fault("an action set created while the last one stands under the same name");
                return XR_ERROR_NAME_DUPLICATED;
            }
            set = _nextHandle++;
            _actionSets.Add(set);
            ActionSetsCreated++;
            return XR_SUCCESS;
        }

        private int DestroyActionSet(ulong set)
        {
            if (!_actionSets.Remove(set))
            {
                Fault("xrDestroyActionSet on a set that is gone");
                return XR_ERROR_HANDLE_INVALID;
            }
            _attachedSets.Remove(set);
            var gone = new List<ulong>();
            foreach (var pair in _actions)
                if (pair.Value == set) gone.Add(pair.Key);
            foreach (ulong action in gone) _actions.Remove(action);
            ActionSetsDestroyed++;
            return XR_SUCCESS;
        }

        private int CreateAction(ulong set, ref XrActionCreateInfo info, out ulong action)
        {
            action = 0;
            if (!_actionSets.Contains(set))
            {
                Fault("xrCreateAction on a set that is gone");
                return XR_ERROR_HANDLE_INVALID;
            }
            if (_attachedSets.Contains(set))
            {
                Fault("xrCreateAction on a set that is attached");
                return XR_ERROR_ACTIONSETS_ALREADY_ATTACHED;
            }
            action = _nextHandle++;
            _actions[action] = set;
            return XR_SUCCESS;
        }

        private int StringToPath(ulong instance, string pathString, out ulong path)
        {
            path = 0;
            if (!CheckInstance(instance, "xrStringToPath")) return XR_ERROR_HANDLE_INVALID;
            if (!_paths.TryGetValue(pathString, out path))
            {
                path = (ulong)_paths.Count + 1;
                _paths[pathString] = path;
            }
            return XR_SUCCESS;
        }

        private int Suggest(ulong instance, ref XrInteractionProfileSuggestedBinding info)
            => CheckInstance(instance, "xrSuggestInteractionProfileBindings") ? SuggestAnswer : XR_ERROR_HANDLE_INVALID;

        private int Attach(ulong session, ref XrSessionActionSetsAttachInfo info)
        {
            if (!CheckSession(session, "xrAttachSessionActionSets")) return XR_ERROR_HANDLE_INVALID;
            if (_sessionAttached)
            {
                Fault("xrAttachSessionActionSets twice on one session");
                return XR_ERROR_ACTIONSETS_ALREADY_ATTACHED;
            }
            if (AttachAnswer < 0) return AttachAnswer;
            for (int i = 0; i < info.countActionSets; i++)
            {
                ulong set = (ulong)Marshal.ReadInt64(info.actionSets, i * sizeof(ulong));
                if (!_actionSets.Contains(set))
                {
                    Fault("xrAttachSessionActionSets names a set that is gone");
                    return XR_ERROR_HANDLE_INVALID;
                }
                _attachedSets.Add(set);
            }
            _sessionAttached = true;
            return AttachAnswer;
        }

        private int CreateActionSpace(ulong session, ref XrActionSpaceCreateInfo info, out ulong space)
        {
            space = 0;
            if (!CheckSession(session, "xrCreateActionSpace")) return XR_ERROR_HANDLE_INVALID;
            if (!_actions.ContainsKey(info.action))
            {
                Fault("xrCreateActionSpace on an action that is gone");
                return XR_ERROR_HANDLE_INVALID;
            }
            if (ActionSpaceAnswer < 0) return ActionSpaceAnswer;
            space = _nextHandle++;
            _spaces.Add(space);
            return ActionSpaceAnswer;
        }

        private int Sync(ulong session, ref XrActionsSyncInfo info)
            => CheckSession(session, "xrSyncActions") ? XR_SUCCESS : XR_ERROR_HANDLE_INVALID;

        private int ActionState(ulong session, ulong action)
        {
            if (!CheckSession(session, "an action state read")) return XR_ERROR_HANDLE_INVALID;
            if (_actions.ContainsKey(action)) return XR_SUCCESS;
            Fault("an action state read on an action that is gone");
            return XR_ERROR_HANDLE_INVALID;
        }

        private int GetPose(ulong session, ref XrActionStateGetInfo getInfo, ref XrActionStatePose state)
        {
            state.isActive = 1;
            return ActionState(session, getInfo.action);
        }

        private int GetFloat(ulong session, ref XrActionStateGetInfo getInfo, ref XrActionStateFloat state)
        {
            state.isActive = 1;
            state.currentState = 0.5f;
            return ActionState(session, getInfo.action);
        }

        private int GetBoolean(ulong session, ref XrActionStateGetInfo getInfo, ref XrActionStateBoolean state)
        {
            state.isActive = 1;
            state.currentState = 1;
            return ActionState(session, getInfo.action);
        }

        private int GetVector2f(ulong session, ref XrActionStateGetInfo getInfo, ref XrActionStateVector2f state)
        {
            state.isActive = 1;
            return ActionState(session, getInfo.action);
        }
    }
}
