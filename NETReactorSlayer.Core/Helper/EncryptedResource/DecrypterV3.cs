﻿using System;
using System.Collections.Generic;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace NETReactorSlayer.Core.Helper;

internal partial class EncryptedResource
{
    private class DecrypterV3 : IDecrypter
    {
        public DecrypterV3(MethodDef method)
        {
            _decrypterMethod = method;
            _locals = new List<Local>(_decrypterMethod.Body.Variables);
            if (!Initialize())
                throw new ApplicationException("Could not initialize decrypter");
        }

        public static bool CouldBeResourceDecrypter(StringCounts stringCounts,
            IEnumerable<string> additionalTypes)
        {
            var requiredTypes = new List<string>
            {
                "System.Reflection.Emit.DynamicMethod",
                "System.Reflection.Emit.ILGenerator"
            };
            requiredTypes.AddRange(additionalTypes);
            return stringCounts.All(requiredTypes);
        }

        public byte[] Decrypt(EmbeddedResource resource)
        {
            var encrypted = resource.CreateReader().ToArray();
            var decrypted = new byte[encrypted.Length];
            var sum = 0U;

            for (var i = 0; i < encrypted.Length; i += 4)
            {
                sum = CalculateMagic(sum);
                WriteUInt32(decrypted, i, sum ^ ReadUInt32(encrypted, i));
            }

            return decrypted;
        }

        private bool Initialize()
        {
            var origInstrs = _decrypterMethod.Body.Instructions;
            if (!Find(origInstrs, out var emuStartIndex, out var emuEndIndex, out _emuLocal) &&
                !FindStartEnd(origInstrs, out emuStartIndex, out emuEndIndex, out _emuLocal))
                return false;
            var count = emuEndIndex - emuStartIndex + 1;
            _instructions = new List<Instruction>(count);
            for (var i = 0; i < count; i++)
                _instructions.Add(origInstrs[emuStartIndex + i].Clone());
            return true;
        }

        private uint CalculateMagic(uint input)
        {
            _instrEmulator.Initialize(_decrypterMethod, _decrypterMethod.Parameters, _locals,
                _decrypterMethod.Body.InitLocals, false);
            _instrEmulator.SetLocal(_emuLocal, new Int32Value((int)input));

            var index = 0;
            while (index < _instructions.Count)
            {
                try
                {
                    if (_decrypterVersion != DecrypterVersion.V69)
                        goto Emulate;
                    if (!_instructions[index].IsLdloc())
                        goto Emulate;
                    if (!TryGetLdcValue(_instructions[index + 1], out var value) || value != 0)
                        goto Emulate;
                    if (!_instructions[index + 2].OpCode.Equals(OpCodes.Bne_Un) &&
                        !_instructions[index + 2].OpCode.Equals(OpCodes.Bne_Un_S))
                        goto Emulate;
                    if (!_instructions[index + 3].IsLdloc())
                        goto Emulate;
                    if (!TryGetLdcValue(_instructions[index + 4], out value) || value != 1)
                        goto Emulate;
                    if (!_instructions[index + 5].OpCode.Equals(OpCodes.Sub))
                        goto Emulate;
                    if (!_instructions[index + 6].IsStloc())
                        goto Emulate;

                    switch (_instrEmulator.GetLocal(CheckLocal(_instructions[index + 6], false).Index))
                    {
                        case Int32Value int32:
                        {
                            if (int32.Value != Int32Value.Zero.Value)
                                index += 7;
                            break;
                        }
                        case Int64Value int64:
                        {
                            if (int64.Value != Int64Value.Zero.Value)
                                index += 7;
                            break;
                        }
                        case Real8Value real8Value:
                        {
                            if (!real8Value.Value.Equals(new Real8Value(0).Value))
                                index += 7;
                            break;
                        }
                    }
                }
                catch { }

                Emulate:
                _instrEmulator.Emulate(_instructions[index]);
                index++;
            }

            if (_instrEmulator.Pop() is not Int32Value tos || !tos.AllBitsValid())
                throw new ApplicationException("Couldn't calculate magic value");
            return (uint)tos.Value;
        }

        private static uint ReadUInt32(byte[] ary, int index)
        {
            var sizeLeft = ary.Length - index;
            if (sizeLeft >= 4)
                return BitConverter.ToUInt32(ary, index);
            return sizeLeft switch
            {
                1 => ary[index],
                2 => (uint)(ary[index] | (ary[index + 1] << 8)),
                3 => (uint)(ary[index] | (ary[index + 1] << 8) | (ary[index + 2] << 16)),
                _ => throw new ApplicationException("Can't read data")
            };
        }

        private static void WriteUInt32(IList<byte> ary, int index, uint value)
        {
            var num = ary.Count - index;
            if (num >= 1)
                ary[index] = (byte)value;
            if (num >= 2)
                ary[index + 1] = (byte)(value >> 8);
            if (num >= 3)
                ary[index + 2] = (byte)(value >> 16);
            if (num >= 4)
                ary[index + 3] = (byte)(value >> 24);
        }

        private bool Find(IList<Instruction> instrs, out int startIndex, out int endIndex, out Local tmpLocal)
        {
            startIndex = 0;
            endIndex = 0;
            tmpLocal = null;
            if (!FindStart(instrs, out var emuStartIndex, out _emuLocal))
                return false;
            if (!FindEnd(instrs, emuStartIndex, out var emuEndIndex))
                return false;
            startIndex = emuStartIndex;
            endIndex = emuEndIndex;
            tmpLocal = _emuLocal;
            return true;
        }

        private bool FindEnd(IList<Instruction> instrs, int startIndex, out int endIndex)
        {
            for (var i = startIndex; i < instrs.Count; i++)
            {
                var instr = instrs[i];
                if (instr.OpCode.FlowControl != FlowControl.Next)
                    break;
                if (!instr.IsStloc() || instr.GetLocal(_locals) != _emuLocal)
                    continue;
                endIndex = i - 1;
                return true;
            }

            endIndex = 0;
            return false;
        }

        private bool FindStart(IList<Instruction> instrs, out int startIndex, out Local tmpLocal)
        {
            var i = 0;
            while (i + 8 < instrs.Count)
            {
                Local local;
                if (instrs[i].OpCode.Code.Equals(Code.Conv_U) && instrs[i + 1].OpCode.Code.Equals(Code.Ldelem_U1) &&
                    instrs[i + 2].OpCode.Code.Equals(Code.Or) && CheckLocal(instrs[i + 3], false) != null &&
                    (local = CheckLocal(instrs[i + 4], true)) != null && CheckLocal(instrs[i + 5], true) != null &&
                    instrs[i + 6].OpCode.Code.Equals(Code.Add) && CheckLocal(instrs[i + 7], false) == local)
                {
                    var instr = instrs[i + 8];
                    var newStartIndex = i + 8;
                    if (instr.IsBr())
                    {
                        instr = instr.Operand as Instruction;
                        newStartIndex = instrs.IndexOf(instr);
                    }

                    if (newStartIndex >= 0 && instr != null && CheckLocal(instr, true) == local)
                    {
                        startIndex = newStartIndex;
                        tmpLocal = local;
                        return true;
                    }
                }

                i++;
            }

            startIndex = 0;
            tmpLocal = null;
            return false;
        }

        private bool FindStartEnd(IList<Instruction> instrs, out int startIndex, out int endIndex,
            out Local tmpLocal)
        {
            var i = 0;
            while (i + 8 < instrs.Count)
            {
                if (instrs[i].OpCode.Code.Equals(Code.Conv_R_Un) &&
                    instrs[i + 1].OpCode.Code.Equals(Code.Conv_R8) &&
                    instrs[i + 2].OpCode.Code.Equals(Code.Conv_U4) &&
                    instrs[i + 3].OpCode.Code.Equals(Code.Add))
                {
                    var newEndIndex = i + 3;
                    var newStartIndex = -1;
                    for (var x = newEndIndex; x > 0; x--)
                        if (instrs[x].OpCode.FlowControl != FlowControl.Next)
                        {
                            if (instrs[x].OpCode.Equals(OpCodes.Bne_Un) ||
                                instrs[x].OpCode.Equals(OpCodes.Bne_Un_S))
                            {
                                _decrypterVersion = DecrypterVersion.V69;
                                continue;
                            }

                            break;
                        }

                    var ckStartIndex = -1;
                    for (var y = newEndIndex; y >= 0; y--)
                        if (instrs[y].IsBr())
                        {
                            if (instrs[y].Operand is not Instruction instr)
                                continue;
                            if (instrs.IndexOf(instr) < y)
                            {
                                if (instrs[y - 1].Operand is not Instruction)
                                    continue;
                                instr = instrs[y - 1].Operand as Instruction;
                                if (instrs.IndexOf(instr) < y)
                                    continue;
                            }
                            newStartIndex = instrs.IndexOf(instr);
                            ckStartIndex = newStartIndex;
                            break;
                        }


                    if (newStartIndex >= 0)
                    {
                        var checkLocs = new List<Local>();
                        for (var y = newEndIndex; y >= newStartIndex; y--)
                            if (CheckLocal(instrs[y], true) is { } loc)
                                if (!checkLocs.Contains(loc))
                                    checkLocs.Add(loc);

                        endIndex = newEndIndex;
                        startIndex = Math.Max(ckStartIndex, newStartIndex);
                        tmpLocal = CheckLocal(instrs[startIndex], true);
                        return true;
                    }
                }

                i++;
            }

            endIndex = 0;
            startIndex = 0;
            tmpLocal = null;
            return false;
        }

        private Local CheckLocal(Instruction instr, bool isLdloc)
        {
            switch (isLdloc)
            {
                case true when !instr.IsLdloc():
                case false when !instr.IsStloc():
                    return null;
                default:
                    return instr.GetLocal(_locals);
            }
        }


        private readonly InstructionEmulator _instrEmulator = new();
        private readonly List<Local> _locals;
        private readonly MethodDef _decrypterMethod;
        private Local _emuLocal;
        private List<Instruction> _instructions;
        private DecrypterVersion _decrypterVersion = DecrypterVersion.V6X;
    }
}