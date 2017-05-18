﻿namespace AtlusScriptLib
{
    public enum FlowScriptOpcode : ushort
    {
        PUSHI,
        PUSHF,
        PUSHIX,
        PUSHIF,
        PUSHREG,
        POPIX,
        POPFX,
        PROC,
        COMM,
        END,
        JUMP,
        CALL,
        RUN,
        GOTO,
        ADD,
        SUB,
        MUL,
        DIV,
        MINUS,
        NOT,
        OR,
        AND,
        EQ,
        NEQ,
        S,
        L,
        SE,
        LE,
        IF,
        PUSHIS,
        PUSHLIX,
        PUSHLFX,
        POPLIX,
        POPLFX,
        PUSHSTR
    }
}