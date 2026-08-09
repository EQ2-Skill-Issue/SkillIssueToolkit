namespace SkillIssueToolkit.ActPlugin
{
    // Plain data from ACT's typed CombatantData/EncounterData properties.
    public class CombatantSnapshot
    {
        public string Name { get; set; }
        public long Damage { get; set; }
        public double DamagePercent { get; set; }
        public double EncDps { get; set; }
        public bool IsYou { get; set; }
        public double CritPercent { get; set; }
        public string MaxHit { get; set; }
        public long Healing { get; set; }
        public double Hps { get; set; }
        public long DamageTaken { get; set; }
        public double DamageTakenPercent { get; set; }
        public int Cures { get; set; }
        public int Deaths { get; set; }
        public long PowerFed { get; set; }
        public long PowerDrain { get; set; }
        public string Class { get; set; }
    }

    public class EncounterSnapshot
    {
        public string EncounterName { get; set; }
        public string Duration { get; set; }
        public long TotalDamage { get; set; }
        public double TotalDps { get; set; }
        public CombatantSnapshot[] Combatants { get; set; }
    }
}