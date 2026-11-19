import { vi } from "@/messages/vi";

export default function HomePage() {
  return (
    <section>
      <h1 className="text-2xl font-bold">{vi.home.title}</h1>
      <p className="mt-2 text-slate-600">{vi.home.description}</p>
    </section>
  );
}
