#include "geo/circle.h"
#include "core/registry.h"

using namespace geo;
namespace c = core;

namespace app {

struct Scene {
    Shape* shapes[4];
    int count = 0;

    void add(Shape* shape) { shapes[count++] = shape; }
    double totalArea() const;
};

double Scene::totalArea() const
{
    double sum = 0;
    for (int i = 0; i < count; ++i)
    {
        sum += shapes[i]->area();
    }

    return sum;
}

class Printer : public Visitor {
public:
    void visit(const Shape& shape) override { last = shape.name(); }
    const char* last = nullptr;
};

double twice(double value) { return value * 2; }

} // namespace app

int main()
{
    app::Scene scene;
    Circle circle(2.0);
    Rect rect(1, 2);
    Square square(3);
    scene.add(&circle);
    scene.add(&rect);
    scene.add(&square);

    auto unit = Circle::unit();
    double area = unit.area() + scene.totalArea();

    Point p{1, 2};
    Point q = p + Point{3, 4};
    q += p;
    circle.move(q);
    circle.move(1.0, 2.0);

    auto* shape = static_cast<Shape*>(&rect);
    area += shape->perimeter();
    area += app::twice(rect.corner(1).at.x);

    app::Printer printer;
    square.accept(printer);

    c::Box<Point> box(p);
    area += box.get().x;

    core::Array<int, 4> values;
    for (auto& v : values)
    {
        v = 1;
    }

    int m = core::max(values[0], values[1]);
    core::Color color = core::Color::Green;
    core::Level level = core::High;
    area += distance(p, midpoint(p, q));

    int big = CORE_MAX(m, 3) + core::total(values);
    if (color == core::Color::Red && level == core::Low)
    {
        return 1;
    }

    auto lambda = [&](const Shape& s) { return s.area() + area; };
    return static_cast<int>(lambda(circle)) + big + core::counter + core::kLimit + Shape::count;
}
