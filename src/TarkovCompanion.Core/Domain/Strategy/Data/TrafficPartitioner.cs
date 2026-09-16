using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.Core.Domain.Strategy.Data;

public static class TrafficPartitioner
{
    private const string AssignmentDomain = "TarkovCompanion.TrafficPartition/v1";

    public static TrafficDataPartition Assign(string partitionGroupId, TrafficPartitionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var groupId = TrafficDataGuard.Sha256(partitionGroupId, nameof(partitionGroupId));
        var basis = string.Join('\n', AssignmentDomain, policy.PolicyVersion, policy.AssignmentSalt, groupId);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        var bucket = BinaryPrimitives.ReadUInt32BigEndian(digest) % TrafficDataBounds.PartitionBasisPoints;
        if (bucket < policy.TrainBasisPoints)
        {
            return TrafficDataPartition.Train;
        }

        return bucket < policy.TrainBasisPoints + policy.TuneBasisPoints
            ? TrafficDataPartition.Tune
            : TrafficDataPartition.HeldOut;
    }
}
