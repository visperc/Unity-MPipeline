﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;
namespace MPipeline
{
    [CreateAssetMenu(menuName = "GPURP Events/Property Set")]
    public sealed unsafe class PropertySetEvent : PipelineEvent
    {
        public CullingResults cullResults;
        public ScriptableCullingParameters cullParams;
        [System.NonSerialized]
        public Vector4[] frustumPlanes = new Vector4[6];
        public float4x4 lastViewProjection { get; private set; }
        public float4x4 inverseLastViewProjection { get; private set; }
        [System.NonSerialized]
        public float4x4 VP;
        [System.NonSerialized]
        public float4x4 inverseVP;
        private Random rand;
        private CalculateMatrixJob calculateJob;
        private JobHandle handle;
        private PerspCam perspCam;
        public float3 frustumMinPoint { get; private set; }
        public float3 frustumMaxPoint { get; private set; }
        [System.NonSerialized]
        public Material overrideOpaqueMaterial;
        private System.Func<PipelineCamera, LastVPData> getLastVP = (c) =>
        {
            float4x4 p = GraphicsUtility.GetGPUProjectionMatrix(c.cam.projectionMatrix, false);
            float4x4 vp = mul(p, c.cam.worldToCameraMatrix);
            return new LastVPData(vp);
        };
        LastVPData lastData;
        public override void PreRenderFrame(PipelineCamera cam, ref PipelineCommandData data)
        {
            if (!cam.cam.TryGetCullingParameters(out cullParams)) return;
            data.buffer.SetInvertCulling(cam.inverseRender);
            cullParams.reflectionProbeSortingCriteria = ReflectionProbeSortingCriteria.ImportanceThenSize;
            cullParams.cullingOptions = CullingOptions.NeedsLighting | CullingOptions.NeedsReflectionProbes;
            if (cam.cam.useOcclusionCulling)
            {
                cullParams.cullingOptions |= CullingOptions.OcclusionCull;
            }
            cullResults = data.context.Cull(ref cullParams);
            for (int i = 0; i < frustumPlanes.Length; ++i)
            {
                Plane p = cullParams.GetCullingPlane(i);
                //GPU Driven RP's frustum plane is inverse from SRP's frustum plane
                frustumPlanes[i] = new Vector4(-p.normal.x, -p.normal.y, -p.normal.z, -p.distance);
            }
            PipelineFunctions.InitRenderTarget(ref cam.targets, cam.cam, data.buffer);
            lastData = IPerCameraData.GetProperty(cam, getLastVP);
            calculateJob.isD3D = GraphicsUtility.platformIsD3D;
            calculateJob.nonJitterP = cam.cam.nonJitteredProjectionMatrix;
            calculateJob.worldToView = cam.cam.worldToCameraMatrix;
            calculateJob.lastVP = lastData.lastVP;
            calculateJob.rand = (Random*)UnsafeUtility.AddressOf(ref rand);
            calculateJob.p = cam.cam.projectionMatrix;
            calculateJob.VP = (float4x4*)UnsafeUtility.AddressOf(ref VP);
            calculateJob.inverseVP = (float4x4*)UnsafeUtility.AddressOf(ref inverseVP);
            Transform camTrans = cam.cam.transform;
            perspCam.forward = camTrans.forward;
            perspCam.up = camTrans.up;
            perspCam.right = camTrans.right;
            perspCam.position = camTrans.position;
            perspCam.nearClipPlane = cam.cam.nearClipPlane;
            perspCam.farClipPlane = cam.cam.farClipPlane;
            perspCam.aspect = cam.cam.aspect;
            perspCam.fov = cam.cam.fieldOfView;
            float3* corners = stackalloc float3[8];
            PipelineFunctions.GetFrustumCorner(ref perspCam, corners);
            frustumMinPoint = corners[0];
            frustumMaxPoint = corners[0];
            for (int i = 1; i < 8; ++i)
            {
                frustumMinPoint = min(frustumMinPoint, corners[i]);
                frustumMaxPoint = max(frustumMaxPoint, corners[i]);
            }
            handle = calculateJob.ScheduleRefBurst();
        }

        public override void FrameUpdate(PipelineCamera cam, ref PipelineCommandData data)
        {
            handle.Complete();
            lastViewProjection = calculateJob.lastVP;
            inverseLastViewProjection = calculateJob.lastInverseVP;
            CommandBuffer buffer = data.buffer;
            buffer.SetKeyword("RENDERING_TEXTURE", cam.cam.targetTexture != null);
            buffer.SetGlobalMatrix(ShaderIDs._LastVp, lastViewProjection);
            buffer.SetGlobalMatrix(ShaderIDs._NonJitterVP, calculateJob.nonJitterVP);
            buffer.SetGlobalMatrix(ShaderIDs._NonJitterTextureVP, calculateJob.nonJitterTextureVP);
            buffer.SetGlobalMatrix(ShaderIDs._InvNonJitterVP, calculateJob.nonJitterInverseVP);
            buffer.SetGlobalMatrix(ShaderIDs._InvVP, inverseVP);
            buffer.SetGlobalVector(ShaderIDs._RandomSeed, calculateJob.randNumber);
            lastData.lastVP = calculateJob.nonJitterVP;
        }
        protected override void Init(PipelineResources resources)
        {
            overrideOpaqueMaterial = new Material(resources.shaders.overrideOpaqueShader);
            rand = new Random((uint)System.Guid.NewGuid().GetHashCode());
            Shader.SetGlobalMatrix(ShaderIDs._LastFrameModel, Matrix4x4.zero);
        }
        protected override void Dispose()
        {
            DestroyImmediate(overrideOpaqueMaterial);
        }
        public override bool CheckProperty()
        {
            return overrideOpaqueMaterial;
        }
        [BurstCompile]
        private struct CalculateMatrixJob : IJob
        {
            public float4x4 nonJitterP;
            public float4x4 p;
            public float4x4 worldToView;
            public float4x4 lastVP;
            public bool isD3D;
            public Random* rand;


            public float4x4* VP;
            public float4x4* inverseVP;
            public float4x4 nonJitterVP;
            public float4x4 nonJitterInverseVP;
            public float4x4 nonJitterTextureVP;
            public float4x4 lastInverseVP;
            public float4 randNumber;
            
            public void Execute()
            {
                randNumber = (float4)(rand->NextDouble4());
                nonJitterVP = mul(GraphicsUtility.GetGPUProjectionMatrix(nonJitterP, false, isD3D), worldToView);
                nonJitterInverseVP = inverse(nonJitterVP);
                nonJitterTextureVP = mul(GraphicsUtility.GetGPUProjectionMatrix(nonJitterP, true, isD3D), worldToView);
                lastInverseVP = inverse(lastVP);
                *VP = mul(GraphicsUtility.GetGPUProjectionMatrix(p, false, isD3D), worldToView);
                *inverseVP = inverse(*VP);
            }
        }
    }

    public class LastVPData : IPerCameraData
    {
        public Matrix4x4 lastVP = Matrix4x4.identity;
        public LastVPData(Matrix4x4 lastVP)
        {
            this.lastVP = lastVP;
        }
        public override void DisposeProperty()
        {
        }
    }
  //  [Unity.Burst.BurstCompile]
    public unsafe struct CustomRendererCullJob : IJobParallelFor
    {
        public NativeList_Int cullResult;
        [NativeDisableUnsafePtrRestriction]
        public float4* frustumPlanes;
        [NativeDisableUnsafePtrRestriction]
        public NativeList_ulong indexBuffer;
        public static void ExecuteInList(NativeList_Int cullResult, float4* frustumPlanes, NativeList_ulong indexBuffer)
        {
            for (int i = 0; i < indexBuffer.Length; ++i)
            {
                CustomDrawRequest.ComponentData* dataPtr = (CustomDrawRequest.ComponentData*)indexBuffer[i];
                if (MathLib.BoxIntersect(ref dataPtr->localToWorldMatrix, dataPtr->boundingBoxPosition, dataPtr->boundingBoxExtents, frustumPlanes, 6))
                    cullResult.ConcurrentAdd(dataPtr->index);

            }
        }
        public void Execute(int index)
        {
            CustomDrawRequest.ComponentData* dataPtr = (CustomDrawRequest.ComponentData*)indexBuffer[index];
            if (MathLib.BoxIntersect(ref dataPtr->localToWorldMatrix, dataPtr->boundingBoxPosition, dataPtr->boundingBoxExtents, frustumPlanes, 6))
                cullResult.ConcurrentAdd(dataPtr->index);
        }
    }
}