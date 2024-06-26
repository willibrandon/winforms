﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace System.Windows.Forms.BinaryFormat.Deserializer;

#pragma warning disable SYSLIB0050 // Type or member is obsolete

/// <summary>
///  General binary format deserializer.
/// </summary>
/// <remarks>
///  <para>
///   This has some constraints over the BinaryFormatter. Notably it does not support
///   <see cref="IObjectReference"/>, which greatly simplifies the deserialization. It
///   also does not allow offset arrays (arrays that have lower bounds other than zero).
///  </para>
///  <para>
///   This deserializer guarantees that all value types are observable in their final
///   state. Object references (including within value types), may not be in their final
///   state until the end of deserialization due to graph cycles. The instance will be
///   the final instance, but it may directly or indirectly contain uncompleted value
///   types. In general it is risky to dereference reference types in <see cref="ISerializable"/>
///   constructors or in <see cref="ISerializationSurrogate"/> call backs.
///  </para>
///  <para>
///   If you need to dereference reference types in <see cref="SerializationInfo"/> waiting
///   for final state by implementing <see cref="IDeserializationCallback"/> or
///   <see cref="OnDeserializedAttribute"/> is the safe way to do so. Surrogates are more
///   complicated (<see cref="ISerializationSurrogate"/>). With surrogates you need to track
///   the instances that need fixup and handle them after invoking the deserializer.
///  </para>
/// </remarks>
/// <devdoc>
///  <see cref="IObjectReference"/> makes deserializing difficult as you don't know the final
///  type until you've finished populating the serialized type. If <see cref="SerializationInfo"/>
///  is involved and you have a cycle you may never be able to complete the deserialization as
///  the reference type values in the <see cref="SerializationInfo"/> can't get the final object.
/// </devdoc>
internal sealed partial class Deserializer : IDeserializer
{
    private readonly IReadOnlyRecordMap _recordMap;
    private readonly BinaryFormattedObject.ITypeResolver _typeResolver;
    BinaryFormattedObject.ITypeResolver IDeserializer.TypeResolver => _typeResolver;

    /// <inheritdoc cref="IDeserializer.Options"/>
    private BinaryFormattedObject.Options Options { get; }
    BinaryFormattedObject.Options IDeserializer.Options => Options;

    /// <inheritdoc cref="IDeserializer.DeserializedObjects"/>
    private readonly Dictionary<int, object> _deserializedObjects = [];
    IDictionary<int, object> IDeserializer.DeserializedObjects => _deserializedObjects;

    // Surrogate cache.
    private readonly Dictionary<Type, ISerializationSurrogate?>? _surrogates;

    // Queue of SerializationInfo objects that need to be applied.
    // These are in depth first order, if there are no cycles in the graph this
    // ensures that all objects are available when the SerializationInfo is applied.
    private Queue<PendingSerializationInfo>? _pendingSerializationInfo;

    // Keeping a separate stack for ids for fast infinite loop checks.
    private readonly Stack<int> _parseStack = [];
    private readonly Stack<ObjectRecordDeserializer> _parserStack = [];

    /// <inheritdoc cref="IDeserializer.IncompleteObjects"/>
    private readonly HashSet<int> _incompleteObjects = [];
    public IReadOnlySet<int> IncompleteObjects => _incompleteObjects;

    // For a given object id, the set of ids that it is waiting on to complete.
    private Dictionary<int, HashSet<int>>? _incompleteDependencies;

    // The pending value updaters. Scanned each time an object is completed.
    private HashSet<ValueUpdater>? _pendingUpdates;

    // Kept as a field to avoid allocating a new one every time we complete objects.
    private readonly Queue<int> _pendingCompletions = [];

    private readonly Id _rootId;

    private event Action<object?>? OnDeserialization;
    private event Action<StreamingContext>? OnDeserialized;

    private Deserializer(
        Id rootId,
        IReadOnlyRecordMap recordMap,
        BinaryFormattedObject.ITypeResolver typeResolver,
        BinaryFormattedObject.Options options)
    {
        _rootId = rootId;
        _recordMap = recordMap;
        _typeResolver = typeResolver;
        Options = options;

        if (Options.SurrogateSelector is not null)
        {
            _surrogates = [];
        }
    }

    /// <summary>
    ///  Deserializes the object graph for the given <paramref name="recordMap"/> and <paramref name="rootId"/>.
    /// </summary>
    [RequiresUnreferencedCode("Calls System.Windows.Forms.BinaryFormat.Deserializer.Deserializer.Deserialize()")]
    internal static object Deserialize(
        Id rootId,
        IReadOnlyRecordMap recordMap,
        BinaryFormattedObject.ITypeResolver typeResolver,
        BinaryFormattedObject.Options options)
    {
        var deserializer = new Deserializer(rootId, recordMap, typeResolver, options);
        return deserializer.Deserialize();
    }

    [RequiresUnreferencedCode("Calls System.Windows.Forms.BinaryFormat.Deserializer.Deserializer.DeserializeRoot(Id)")]
    private object Deserialize()
    {
        DeserializeRoot(_rootId);

        object root = _deserializedObjects[_rootId];

        // Complete all pending SerializationInfo objects.
        int pendingCount = _pendingSerializationInfo?.Count ?? 0;
        while (_pendingSerializationInfo is not null && _pendingSerializationInfo.TryDequeue(out PendingSerializationInfo? pending))
        {
            // Using pendingCount to only requeue on the first pass.
            if (--pendingCount >= 0
                && _pendingSerializationInfo.Count != 0
                && _incompleteDependencies is not null
                && _incompleteDependencies.TryGetValue(pending.ObjectId, out HashSet<int>? dependencies))
            {
                // We can get here with nested ISerializable value types.

                // Hopefully another pass will complete this.
                if (dependencies.Count > 0)
                {
                    _pendingSerializationInfo.Enqueue(pending);
                    continue;
                }

                Debug.Fail("Completed dependencies should have been removed from the dictionary.");
            }

            // All _pendingSerializationInfo objects are considered incomplete.
            pending.Populate(_deserializedObjects, Options.StreamingContext);
            ((IDeserializer)this).CompleteObject(pending.ObjectId);
        }

        if (_incompleteObjects.Count > 0 || (_pendingUpdates is not null && _pendingUpdates.Count > 0))
        {
            // This should never happen.
            throw new SerializationException("Objects could not be deserialized completely.");
        }

        // Notify [OnDeserialized] instance methods for all relevant deserialized objects,
        // then callback IDeserializationCallback on all objects that implement it.
        OnDeserialization?.Invoke(null);
        OnDeserialized?.Invoke(Options.StreamingContext);

        return root;
    }

    [RequiresUnreferencedCode("Calls DeserializeNew(Id)")]
    private void DeserializeRoot(Id rootId)
    {
        object root = DeserializeNew(rootId);
        if (root is not ObjectRecordDeserializer parser)
        {
            return;
        }

        _parseStack.Push(rootId);
        _parserStack.Push(parser);

        while (_parserStack.TryPop(out ObjectRecordDeserializer? currentParser))
        {
            int currentId = _parseStack.Pop();
            Debug.Assert(currentId == currentParser.ObjectRecord.ObjectId);

            Id requiredId;
            while (!(requiredId = currentParser.Continue()).IsNull)
            {
                // A record is required to complete the current parser. Get it.
                object requiredObject = DeserializeNew(requiredId);
                Debug.Assert(requiredObject is not IRecord);

                if (requiredObject is ObjectRecordDeserializer requiredParser)
                {
                    // The required object is not complete.

                    if (_parseStack.Contains(requiredId))
                    {
                        // All objects should be available before they're asked for a second time.
                        throw new SerializationException("Unexpected parser cycle.");
                    }

                    // Push our current parser.
                    _parseStack.Push(currentId);
                    _parserStack.Push(currentParser);

                    // Push the required parser so we can complete it.
                    _parseStack.Push(requiredId);
                    _parserStack.Push(requiredParser);

                    break;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [RequiresUnreferencedCode("Calls System.Windows.Forms.BinaryFormat.Deserializer.ObjectRecordParser.Create(Id, IRecord, IDeserializer)")]
        object DeserializeNew(Id id)
        {
            // Strings, string arrays, and primitive arrays can be completed without creating a
            // parser object. Single primitives don't normally show up as records unless they are top
            // level or are boxed into an interface reference. Checking for these requires costly
            // string matches and as such we'll just create the parser object.

            IRecord record = _recordMap[id];
            if (record is BinaryObjectString binaryString)
            {
                _deserializedObjects.Add(id, binaryString.Value);
                return binaryString.Value;
            }

            if (record is ArrayRecord arrayRecord)
            {
                Array? values = arrayRecord switch
                {
                    ArraySingleString stringArray => stringArray.GetStringValues(_recordMap).ToArray(),
                    IPrimitiveTypeRecord primitiveArray => primitiveArray.GetPrimitiveArray(),
                    _ => null
                };

                if (values is not null)
                {
                    _deserializedObjects.Add(arrayRecord.ObjectId, values);
                    return values;
                }
            }

            // Not a simple case, need to do a full deserialization of the record.
            _incompleteObjects.Add(id);

            var deserializer = ObjectRecordDeserializer.Create(id, record, this);

            // Add the object as soon as possible to support circular references.
            _deserializedObjects.Add(id, deserializer.Object);
            return deserializer;
        }
    }

    ISerializationSurrogate? IDeserializer.GetSurrogate(Type type)
    {
        // If we decide not to cache, this method could be moved to the callsite.

        if (_surrogates is null)
        {
            return null;
        }

        Debug.Assert(Options.SurrogateSelector is not null);

        if (!_surrogates.TryGetValue(type, out ISerializationSurrogate? surrogate))
        {
            surrogate = Options.SurrogateSelector.GetSurrogate(type, Options.StreamingContext, out _);
            _surrogates[type] = surrogate;
        }

        return surrogate;
    }

    void IDeserializer.PendSerializationInfo(PendingSerializationInfo pending)
    {
        _pendingSerializationInfo ??= new();
        _pendingSerializationInfo.Enqueue(pending);
    }

    void IDeserializer.PendValueUpdater(ValueUpdater updater)
    {
        // Add the pending update and update the dependencies list.

        _pendingUpdates ??= [];
        _pendingUpdates.Add(updater);

        _incompleteDependencies ??= [];

        if (_incompleteDependencies.TryGetValue(updater.ObjectId, out HashSet<int>? dependencies))
        {
            dependencies.Add(updater.ValueId);
        }
        else
        {
            _incompleteDependencies.Add(updater.ObjectId, [updater.ValueId]);
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "The type is already in the cache of the TypeResolver, no need to mark this one again.")]
    void IDeserializer.CompleteObject(Id id)
    {
        // Need to use a queue as Completion is recursive.

        _pendingCompletions.Enqueue(id);
        Id completed = Id.Null;

        while (_pendingCompletions.TryDequeue(out int completedId))
        {
            _incompleteObjects.Remove(completedId);

            if (_recordMap[completedId] is ClassRecord classRecord)
            {
                // Hook any finished events for this object. Doing at the end of deserialization for simplicity.
                // (If we knew there were no cycles in the graph we could fire these as we go.)

                Type type = _typeResolver.GetType(classRecord.Name, classRecord.LibraryId);
                object @object = _deserializedObjects[completedId];

                OnDeserialized += SerializationEvents.GetOnDeserializedForType(type, @object);

                if (@object is IDeserializationCallback callback)
                {
                    OnDeserialization += callback.OnDeserialization;
                }
            }

            if (_incompleteDependencies is null)
            {
                continue;
            }

            Debug.Assert(_pendingUpdates is not null);

            // When we've recursed, we've done so because there are no more dependencies for the current
            // id, so we can remove it from the dictionary. We have to pend as we're iterating the dictionary.
            if (!completed.IsNull)
            {
                _incompleteDependencies.Remove(completed);
                completed = Id.Null;
            }

            foreach ((int incompleteId, HashSet<int> dependencies) in _incompleteDependencies)
            {
                if (!dependencies.Remove(completedId))
                {
                    continue;
                }

                // Search for fixups that need to be applied for this dependency.
                int removals = _pendingUpdates.RemoveWhere((ValueUpdater updater) =>
                {
                    if (updater.ValueId != completedId)
                    {
                        return false;
                    }

                    updater.UpdateValue(_deserializedObjects);
                    return true;
                });

                if (dependencies.Count != 0)
                {
                    continue;
                }

                // No more dependencies, enqueue for completion
                completed = incompleteId;
                _pendingCompletions.Enqueue(incompleteId);
            }
        }
    }
}

#pragma warning restore SYSLIB0050 // Type or member is obsolete
