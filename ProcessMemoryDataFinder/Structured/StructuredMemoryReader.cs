using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ProcessMemoryDataFinder.API;
using ProcessMemoryDataFinder.Structured.Tokenizer;

namespace ProcessMemoryDataFinder.Structured
{
    public class StructuredMemoryReader : IDisposable, IStructuredMemoryReader
    {
        protected MemoryReader _memoryReader;
        protected AddressFinder _addressFinder;
        private AddressTokenizer _addressTokenizer = new AddressTokenizer();
        protected IObjectReader ObjectReader;

        /// <summary>
        /// Should memory read times be tracked and saved in <see cref="ReadTimes"/>?
        /// </summary>
        public bool WithTimes { get; set; }
        /// <summary>
        /// When <see cref="WithTimes"/> is true, stores per-prop read times
        /// </summary>
        public Dictionary<string, double> ReadTimes { get; } = new Dictionary<string, double>();

        public bool CanRead => _memoryReader.CurrentProcess != null;

        public event EventHandler<(object readObject, string propPath)> InvalidRead;

        public bool AbortReadOnInvalidValue { get; set; } = true;

        private class DummyAddressChecker
        {
            public int? AddressProp { get; set; }
        }

        protected class TypeCacheEntry
        {
            public Type Type { get; }
            public string ClassPath { get; }
            public List<PropInfo> Props { get; }
            public PropInfo ReadCheckerPropertyInfo { get; set; }

            public TypeCacheEntry(Type type, string classPath, List<PropInfo> props)
            {
                Type = type;
                ClassPath = classPath;
                Props = props;
            }
        }

        private Dictionary<object, TypeCacheEntry> typeCache =
            new Dictionary<object, TypeCacheEntry>();
        protected Dictionary<Type, object> DefaultValues = new Dictionary<Type, object>
        {
            { typeof(int) , -1 },
            { typeof(short) , (short)-1 },
            { typeof(ushort) , (ushort)0 },
            { typeof(float) , -1f },
            { typeof(double) , -1d },
            { typeof(bool) , false },
            { typeof(string) , null },
            { typeof(int[]) , null },
            { typeof(List<int>) , null },
        };
        protected Dictionary<Type, uint> SizeDictionary = new Dictionary<Type, uint>
        {
            { typeof(int) , 4 },
            { typeof(short) , 2 },
            { typeof(ushort) , 2 },
            { typeof(float) , 8 },
            { typeof(double) , 8 },
            { typeof(long) , 8 },
            { typeof(bool) , 1 },

            { typeof(string) , 0 },
            { typeof(int[]) , 0 },
            { typeof(List<int>) , 0 },
        };
        public StructuredMemoryReader(string processName, Dictionary<string, string> baseAdresses, string mainWindowTitleHint = null, MemoryReader memoryReader = null, IObjectReader objectReader = null)
        {
            _memoryReader = memoryReader ?? new MemoryReader(processName, mainWindowTitleHint);
            _addressFinder = new AddressFinder(_memoryReader, baseAdresses);
            ObjectReader = objectReader ?? new ObjectReader(_memoryReader);

        }

        public bool TryReadProperty(object readObj, string propertyName, out object result)
        {
            var cacheEntry = typeCache[PrepareReadObject(readObj, string.Empty)];
            var propInfo = cacheEntry.Props.FirstOrDefault(p => p.PropertyInfo.Name == propertyName);
            if (propInfo == null)
            {
                result = null;
                return false;
            }

            var resolvedProp = ResolveProp(readObj, IntPtr.Zero, propInfo, cacheEntry);
            result = resolvedProp.PropValue;
            return !resolvedProp.InvalidRead;
        }

        public bool TryRead<T>(T readObj) where T : class
        {
            if (readObj == null)
                throw new NullReferenceException();

            _addressFinder.ResetGroupReadCache();
            return TryInternalRead(readObj);
        }

        protected bool TryInternalRead<T>(T readObj, IntPtr? classAddress = null, string classPath = null) where T : class
        {
            var cacheEntry = typeCache[PrepareReadObject(readObj, classPath)];

            if (cacheEntry.ReadCheckerPropertyInfo != null)
            {
                var resolvedProp = ResolveProp(readObj, classAddress, cacheEntry.ReadCheckerPropertyInfo, cacheEntry);
                SetPropValue(cacheEntry.ReadCheckerPropertyInfo, resolvedProp.PropValue);
                classAddress = resolvedProp.ClassAdress;
                if (resolvedProp.InvalidRead || resolvedProp.PropValue == null || (int?)resolvedProp.PropValue == 0)
                    return false;
            }

            foreach (var prop in cacheEntry.Props)
            {
                Stopwatch readStopwatch = null;
                if (WithTimes)
                    readStopwatch = Stopwatch.StartNew();

                var result = ResolveProp(readObj, classAddress, prop, cacheEntry);
                classAddress = result.ClassAdress;
                if (result.InvalidRead && AbortReadOnInvalidValue && !prop.IgnoreNullPtr)
                {
                    readStopwatch?.Stop();
                    InvalidRead?.Invoke(this, (readObj, prop.Path));
                    return false;
                }

                SetPropValue(prop, result.PropValue);
                if (WithTimes && readStopwatch != null)
                {
                    readStopwatch.Stop();
                    var readTimeMs = readStopwatch.ElapsedTicks / (double)TimeSpan.TicksPerMillisecond;
                    ReadTimes[prop.Path] = readTimeMs;
                }
            }

            return true;
        }

        private (IntPtr? ClassAdress, bool InvalidRead, object PropValue) ResolveProp<T>(T readObj, IntPtr? classAddress, PropInfo prop, TypeCacheEntry cacheEntry) where T : class
        {
            if (prop.IsClass && !prop.IsStringOrArrayOrList)
            {
                var propValue = prop.PropertyInfo.GetValue(readObj);
                if (propValue == null)
                    return (classAddress, false, null);

                IntPtr? address = prop.MemoryPath == null
                    ? (IntPtr?)null
                    : ResolvePath(cacheEntry.ClassPath, prop.MemoryPath, classAddress).FinalAddress;

                if (address == IntPtr.Zero)
                    return (classAddress, !prop.IgnoreNullPtr, propValue);

                var readSuccessful = TryInternalRead(propValue, address, prop.Path);
                return (classAddress, !readSuccessful, propValue);
            }

            var result = ReadValueForPropInMemory(classAddress, prop, cacheEntry);
            return (result.ClassAddress, result.InvalidRead, result.Result);
        }

        private (object Result, IntPtr ClassAddress, bool InvalidRead) ReadValueForPropInMemory(IntPtr? classAddress, PropInfo prop, TypeCacheEntry cacheEntry)
        {
            var (propAddress, newClassAddress) = ResolvePath(cacheEntry.ClassPath, prop.MemoryPath, classAddress);
            var result = ReadObjectAt(propAddress, prop);

            return (result, newClassAddress, (prop.IsClass || prop.IsNullable) ? false : result == null);
        }

        private void SetPropValue(PropInfo prop, object result)
        {
            if (result != null || prop.IsNullable)
                prop.Setter(result);
            else if (DefaultValues.ContainsKey(prop.PropType))
                prop.Setter(DefaultValues[prop.PropType]);
        }

        private T PrepareReadObject<T>(T readObject, string classPath)
        {
            if (typeCache.ContainsKey(readObject)) return readObject;

            var type = readObject.GetType();
            var classProps = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var classMemoryAddressAttribute = type.GetCustomAttribute(typeof(MemoryAddressAttribute), true) as MemoryAddressAttribute;

            typeCache[readObject] = new TypeCacheEntry(type, classMemoryAddressAttribute?.RelativePath, new List<PropInfo>());

            if (classMemoryAddressAttribute != null && classMemoryAddressAttribute.CheckClassAddress)
            {
                var dummyProp = typeof(DummyAddressChecker).GetProperty(nameof(DummyAddressChecker.AddressProp));
                if (string.IsNullOrEmpty(classMemoryAddressAttribute.CheckClassAddressPropName))
                    typeCache[readObject].ReadCheckerPropertyInfo = CreatePropInfo(readObject, dummyProp, classPath, type.Name, true);
                else
                    typeCache[readObject].ReadCheckerPropertyInfo = CreatePropInfo(readObject, classProps.First(x => x.Name == classMemoryAddressAttribute.CheckClassAddressPropName), classPath, type.Name);
            }

            foreach (var prop in classProps.Where(x => x.Name != classMemoryAddressAttribute?.CheckClassAddressPropName))
            {
                var propInfo = CreatePropInfo(readObject, prop, classPath, type.Name);
                if (propInfo != null)
                    typeCache[readObject].Props.Add(propInfo);
            }

            return readObject;
        }

        private PropInfo CreatePropInfo<T>(T readObject, PropertyInfo propertyInfo, string classPath, string parentTypeName, bool classAddressGetter = false)
        {
            if (!(propertyInfo.GetCustomAttribute(typeof(MemoryAddressAttribute), true) is MemoryAddressAttribute memoryAddressAttribute))
                return null;

            string finalPath = $"{parentTypeName}.{propertyInfo.Name}";
            if (!string.IsNullOrEmpty(classPath))
                finalPath = $"{classPath}.{finalPath}";
            if (classAddressGetter)
                return new PropInfo(finalPath, propertyInfo, propertyInfo.PropertyType, Nullable.GetUnderlyingType(propertyInfo.PropertyType) ?? propertyInfo.PropertyType, propertyInfo.PropertyType.IsClass,
                    propertyInfo.PropertyType == typeof(string) || (propertyInfo.PropertyType.IsGenericType && propertyInfo.PropertyType.GetGenericTypeDefinition() == typeof(List<>)),
                    Nullable.GetUnderlyingType(propertyInfo.PropertyType) != null, "", false, (v) => { }, () => null);

            return new PropInfo(finalPath, propertyInfo, propertyInfo.PropertyType, Nullable.GetUnderlyingType(propertyInfo.PropertyType) ?? propertyInfo.PropertyType, propertyInfo.PropertyType.IsClass,
                propertyInfo.PropertyType == typeof(string) || (propertyInfo.PropertyType.IsGenericType && propertyInfo.PropertyType.GetGenericTypeDefinition() == typeof(List<>)),
                Nullable.GetUnderlyingType(propertyInfo.PropertyType) != null, memoryAddressAttribute.RelativePath, memoryAddressAttribute.IgnoreNullPtr, (v) => propertyInfo.SetValue(readObject, v), () => propertyInfo.GetValue(readObject));
        }

        protected virtual object ReadObjectAt(IntPtr finalAddress, PropInfo propInfo)
        {
            if (finalAddress == IntPtr.Zero) return null;

            var type = propInfo.UnderlyingPropType;
            if (type == typeof(string))
                return ObjectReader.ReadUnicodeString(finalAddress);
            if (type == typeof(int[]))
                return ObjectReader.ReadIntArray(finalAddress);
            if (type == typeof(List<int>))
                return ObjectReader.ReadIntList(finalAddress);

            if (!SizeDictionary.ContainsKey(type))
                throw new NotImplementedException($"Prop {propInfo.Path} with type {type?.FullName} doesn't have its size set in SizeDictionary/DefaultValues. Override ReadObjectAt to read custom object lists/arrays.");

            var propValue = _memoryReader.ReadData(finalAddress, SizeDictionary[type]);
            if (propValue == null)
                return null;

            if (type == typeof(int))
                return BitConverter.ToInt32(propValue, 0);
            if (type == typeof(float))
                return BitConverter.ToSingle(propValue, 0);
            if (type == typeof(double))
                return BitConverter.ToDouble(propValue, 0);
            if (type == typeof(short))
                return BitConverter.ToInt16(propValue, 0);
            if (type == typeof(ushort))
                return BitConverter.ToUInt16(propValue, 0);
            if (type == typeof(bool))
                return BitConverter.ToBoolean(propValue, 0);
            if (type == typeof(long))
                return BitConverter.ToInt64(propValue, 0);
            throw new NotImplementedException($"Reading of {type?.FullName} is not implemented. Override ReadObjectAt.");
        }

        private (IntPtr FinalAddress, IntPtr ClassAddress) ResolvePath(string classMemoryPath, string propMemoryPath,
            IntPtr? providedClassAddress)
        {
            IntPtr classAddress = IntPtr.Zero;
            if (providedClassAddress.HasValue && providedClassAddress.Value != IntPtr.Zero)
                classAddress = providedClassAddress.Value;
            else if (classMemoryPath != null)
            {
                var tokenizedClass = _addressTokenizer.Tokenize(classMemoryPath);
                classAddress = _addressFinder.FindAddress(tokenizedClass, IntPtr.Zero);
                if (classAddress == IntPtr.Zero) return (IntPtr.Zero, IntPtr.Zero);
            }

            IntPtr finalAddress = IntPtr.Zero;
            if (propMemoryPath != null)
            {
                var tokenizedProp = _addressTokenizer.Tokenize(propMemoryPath);
                finalAddress = _addressFinder.FindAddress(tokenizedProp, classAddress);
            }

            return (finalAddress, classAddress);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _memoryReader?.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}