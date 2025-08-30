using System;
using UnityEngine;

[Serializable]
public struct MinMaxf
{
    public float min, max;
    public float Lerp01(float t) => Mathf.Lerp(min, max, t);
    public float Random(System.Random r) => Mathf.Lerp(min, max, (float)r.NextDouble());
}

[Serializable]
public struct MinMaxi
{
    public int min, max;
    public int Random(System.Random r) => r.Next(min, max + 1);
}
